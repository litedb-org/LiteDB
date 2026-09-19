using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2811_Tests
    {
        public enum WritePath
        {
            InsertOne,
            InsertMany,
            UpsertOne,
            UpsertMany
        }

        public sealed class IntRow
        {
            public int Id { get; }
            public string Name { get; }

            public IntRow(int id, string name)
            {
                Id = id;
                Name = name;
            }
        }

        public sealed class ObjectIdRow
        {
            public ObjectId Id { get; }
            public string Name { get; }

            public ObjectIdRow(ObjectId id, string name)
            {
                Id = id;
                Name = name;
            }
        }

        [Theory]
        [InlineData(WritePath.InsertOne)]
        [InlineData(WritePath.InsertMany)]
        [InlineData(WritePath.UpsertOne)]
        [InlineData(WritePath.UpsertMany)]
        public void Read_only_int_auto_id_is_atomic_for_every_typed_write_path(WritePath path)
        {
            VerifyAtomicAutoId(
                path,
                91,
                name => new IntRow(0, name),
                row => row.Id,
                id =>
                {
                    id.IsInt32.Should().BeTrue();
                    id.AsInt32.Should().BePositive().And.NotBe(91);
                });
        }

        [Theory]
        [InlineData(WritePath.InsertOne)]
        [InlineData(WritePath.InsertMany)]
        [InlineData(WritePath.UpsertOne)]
        [InlineData(WritePath.UpsertMany)]
        public void Read_only_object_id_is_atomic_for_every_typed_write_path(WritePath path)
        {
            var controlId = new ObjectId("0102030405060708090a0b0c");

            VerifyAtomicAutoId(
                path,
                controlId,
                name => new ObjectIdRow(ObjectId.Empty, name),
                row => row.Id,
                id =>
                {
                    id.IsObjectId.Should().BeTrue();
                    id.AsObjectId.Should().NotBe(ObjectId.Empty).And.NotBe(controlId);
                });
        }

        private static void VerifyAtomicAutoId<T>(
            WritePath path,
            BsonValue controlId,
            Func<string, T> createRow,
            Func<T, BsonValue> readEntityId,
            Action<BsonValue> assertGeneratedId)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                db.GetCollection<T>("rows").Insert(controlId, createRow("control"));
                db.Checkpoint();
            }

            var baselineBytes = File.ReadAllBytes(file.Filename);
            using (var observer = new LiteDatabase(file.Filename))
            {
                ReadRawRows(observer).Single()["_id"].Should().Be(controlId);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(baselineBytes,
                "an independent read-only observation must not mutate the durability baseline");

            var targetCount = IsMany(path) ? 2 : 1;
            var targets = Enumerable.Range(0, targetCount)
                .Select(index => createRow("target-" + (char)('a' + index)))
                .ToArray();
            var result = new WriteResult();
            Exception failure;
            BsonDocument[] immediateRows;

            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var collection = db.GetCollection<T>("rows");
                failure = Record.Exception(() => ExecuteWrite(collection, path, targets, result));
                immediateRows = ReadRawRows(db);
            }

            var bytesAfterAttempt = File.ReadAllBytes(file.Filename);
            BsonDocument[] durableRows;
            using (var independentObserver = new LiteDatabase(file.Filename))
            {
                durableRows = ReadRawRows(independentObserver);
            }

            using (new AssertionScope())
            {
                Fingerprints(immediateRows).Should().Equal(Fingerprints(durableRows),
                    "the caller and a fresh connection must observe the same committed ledger");

                if (failure == null)
                {
                    result.Affected.Should().Be(targetCount);
                    AssertSuccessfulLedger(durableRows, controlId, targets, readEntityId, assertGeneratedId);
                    bytesAfterAttempt.Should().NotEqual(baselineBytes,
                        "a successful insert or upsert must durably add the target rows");

                    if (result.ReturnedId != null)
                    {
                        var target = durableRows.Single(row => row["Name"].AsString == "target-a");
                        result.ReturnedId.Should().Be(target["_id"]);
                    }
                }
                else
                {
                    AssertClearReadOnlyFailure(failure);
                    AssertOnlyControlRemains(durableRows, controlId);
                    bytesAfterAttempt.Should().Equal(baselineBytes,
                        "validation must happen before writing; write-then-delete compensation is not atomic");
                }
            }
        }

        private static void ExecuteWrite<T>(
            ILiteCollection<T> collection,
            WritePath path,
            IReadOnlyList<T> targets,
            WriteResult result)
        {
            switch (path)
            {
                case WritePath.InsertOne:
                    result.ReturnedId = collection.Insert(targets[0]);
                    result.Affected = 1;
                    break;
                case WritePath.InsertMany:
                    result.Affected = collection.Insert(targets);
                    break;
                case WritePath.UpsertOne:
                    result.Affected = collection.Upsert(targets[0]) ? 1 : 0;
                    break;
                case WritePath.UpsertMany:
                    result.Affected = collection.Upsert(targets);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(path));
            }
        }

        private static void AssertSuccessfulLedger<T>(
            BsonDocument[] rows,
            BsonValue controlId,
            IReadOnlyList<T> targets,
            Func<T, BsonValue> readEntityId,
            Action<BsonValue> assertGeneratedId)
        {
            var expectedNames = new[] { "control" }
                .Concat(targets.Select((_, index) => "target-" + (char)('a' + index)))
                .OrderBy(name => name);
            rows.Select(row => row["Name"].AsString).Should().Equal(expectedNames);
            rows.Single(row => row["Name"].AsString == "control")["_id"].Should().Be(controlId);

            var targetRows = rows.Where(row => row["Name"].AsString != "control").ToArray();
            targetRows.Select(row => row["_id"]).Should().OnlyHaveUniqueItems();
            foreach (var row in rows)
            {
                row.Keys.Should().BeEquivalentTo(new[] { "_id", "Name" });
            }
            foreach (var row in targetRows)
            {
                assertGeneratedId(row["_id"]);
            }

            for (var index = 0; index < targets.Count; index++)
            {
                var entityId = readEntityId(targets[index]);
                if (!entityId.IsNull && entityId != BsonValue.Null && !IsEmptyAutoId(entityId))
                {
                    var name = "target-" + (char)('a' + index);
                    targetRows.Single(row => row["Name"].AsString == name)["_id"].Should().Be(entityId,
                        "an id copied back to an entity must identify that entity's durable row");
                }
            }
        }

        private static void AssertOnlyControlRemains(BsonDocument[] rows, BsonValue controlId)
        {
            rows.Should().ContainSingle();
            rows[0].Keys.Should().BeEquivalentTo(new[] { "_id", "Name" });
            rows[0]["_id"].Should().Be(controlId);
            rows[0]["Name"].AsString.Should().Be("control");
        }

        private static void AssertClearReadOnlyFailure(Exception failure)
        {
            failure.Should().BeOfType<LiteException>(
                "a null dereference or generic argument error does not identify the invalid id member");
            var message = failure.Message;
            message.Should().Contain("Id", "the diagnostic must identify the member that cannot be assigned");
            (message.IndexOf("read-only", StringComparison.OrdinalIgnoreCase) >= 0 ||
             message.IndexOf("setter", StringComparison.OrdinalIgnoreCase) >= 0)
                .Should().BeTrue("the diagnostic must explain that the Id member cannot be written");
        }

        private static BsonDocument[] ReadRawRows(ILiteDatabase db)
        {
            return db.GetCollection("rows").FindAll()
                .OrderBy(row => row["Name"].AsString)
                .ToArray();
        }

        private static string[] Fingerprints(IEnumerable<BsonDocument> rows)
        {
            return rows.Select(row => row["Name"].AsString + "|" + row["_id"].ToString())
                .OrderBy(value => value)
                .ToArray();
        }

        private static bool IsMany(WritePath path)
        {
            return path == WritePath.InsertMany || path == WritePath.UpsertMany;
        }

        private static bool IsEmptyAutoId(BsonValue id)
        {
            return (id.IsInt32 && id.AsInt32 == 0) ||
                (id.IsObjectId && id.AsObjectId == ObjectId.Empty);
        }

        private sealed class WriteResult
        {
            public int Affected { get; set; }
            public BsonValue ReturnedId { get; set; }
        }
    }
}
