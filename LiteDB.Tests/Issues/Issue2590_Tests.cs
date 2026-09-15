using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2590_Tests
    {
        private const string HexControlId = "0102030405060708090a0b0c";

        public enum WritePath
        {
            InsertOne,
            InsertMany,
            UpsertOne,
            UpsertMany
        }

        public sealed class Row
        {
            public string Id { get; set; }
            public string Name { get; set; }

            public Row(string id, string name)
            {
                Id = id;
                Name = name;
            }

            public Row(string name)
                : this(string.Empty, name)
            {
            }
        }

        [Theory]
        [InlineData(WritePath.InsertOne)]
        [InlineData(WritePath.InsertMany)]
        [InlineData(WritePath.UpsertOne)]
        [InlineData(WritePath.UpsertMany)]
        public void Empty_string_ids_are_addressable_or_rejected_before_any_write(WritePath path)
        {
            using var file = new TempFile();
            var objectControlId = new ObjectId(HexControlId);

            using (var db = new LiteDatabase(file.Filename))
            {
                var typed = db.GetCollection<Row>("rows");
                typed.Insert(new Row(HexControlId, "string-control"));
                typed.Insert(new Row("ordinary-control", "ordinary-control"));
                db.GetCollection("rows").Insert(new BsonDocument
                {
                    ["_id"] = objectControlId,
                    ["Name"] = "object-control"
                });
                db.Checkpoint();
            }

            var baseline = ReadRows(file.Filename);
            var baselineBytes = File.ReadAllBytes(file.Filename);
            var targets = new[] { new Row("target-a"), new Row("target-b") };
            var result = new WriteResult();
            Exception failure;
            BsonDocument[] immediate;

            using (var db = new LiteDatabase(file.Filename))
            {
                var collection = db.GetCollection<Row>("rows");
                failure = Record.Exception(() => Execute(collection, targets, path, result));
                immediate = ReadRows(db);
            }

            var bytesAfterAttempt = File.ReadAllBytes(file.Filename);
            var durable = ReadRows(file.Filename);

            using (new AssertionScope())
            {
                Fingerprints(immediate).Should().Equal(Fingerprints(durable),
                    "the writer and a fresh observer must agree about the committed ledger");

                if (failure == null)
                {
                    result.Affected.Should().Be(2);
                    bytesAfterAttempt.Should().NotEqual(baselineBytes,
                        "a supported write must durably add both target rows");
                    AssertSupportedResult(file.Filename, path, targets, result, objectControlId);
                }
                else
                {
                    AssertDocumentedRejection(failure);
                    Fingerprints(durable).Should().Equal(Fingerprints(baseline),
                        "a rejected auto-id cannot leave a hidden ObjectId row behind");
                    bytesAfterAttempt.Should().Equal(baselineBytes,
                        "validation must precede all page writes, not write and compensate later");
                    targets.Should().OnlyContain(row => row.Id == string.Empty,
                        "a rejected operation cannot claim that it assigned an id");
                }
            }
        }

        private static void Execute(
            ILiteCollection<Row> collection,
            IReadOnlyList<Row> targets,
            WritePath path,
            WriteResult result)
        {
            switch (path)
            {
                case WritePath.InsertOne:
                    foreach (var target in targets)
                    {
                        result.ReturnedIds.Add(collection.Insert(target));
                        result.Affected++;
                    }
                    break;
                case WritePath.InsertMany:
                    result.Affected = collection.Insert(targets);
                    break;
                case WritePath.UpsertOne:
                    result.Affected = targets.Count(target => collection.Upsert(target));
                    break;
                case WritePath.UpsertMany:
                    result.Affected = collection.Upsert(targets);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(path));
            }
        }

        private static void AssertSupportedResult(
            string filename,
            WritePath path,
            IReadOnlyList<Row> targets,
            WriteResult result,
            ObjectId objectControlId)
        {
            targets.Select(row => row.Id).Should().OnlyHaveUniqueItems();

            using var reopened = new LiteDatabase(filename);
            var raw = reopened.GetCollection("rows");
            var typed = reopened.GetCollection<Row>("rows");

            raw.Count().Should().Be(5);
            AssertControls(raw, typed, objectControlId);

            foreach (var target in targets)
            {
                target.Id.Should().NotBeNullOrEmpty();
                new ObjectId(target.Id).ToString().Should().Be(target.Id,
                    "a generated string id must be an exact ObjectId text representation");

                var stored = raw.FindById(target.Id);
                Assert.NotNull(stored);
                stored["_id"].IsString.Should().BeTrue(
                    "a string member cannot be backed by an unqueryable ObjectId key");
                stored["Name"].AsString.Should().Be(target.Name);
                Assert.Null(raw.FindById(new ObjectId(target.Id)));

                typed.FindById(target.Id).Name.Should().Be(target.Name);
                typed.FindOne(row => row.Id == target.Id).Name.Should().Be(target.Name);
                typed.Find(Query.EQ("_id", target.Id)).Single().Name.Should().Be(target.Name);
            }

            if (path == WritePath.InsertOne)
            {
                result.ReturnedIds.Should().HaveCount(2);
                result.ReturnedIds.Select(id => id.IsString).Should().OnlyContain(value => value);
                result.ReturnedIds.Select(id => id.AsString).Should().Equal(targets.Select(row => row.Id));
            }
        }

        private static void AssertControls(
            ILiteCollection<BsonDocument> raw,
            ILiteCollection<Row> typed,
            ObjectId objectControlId)
        {
            var stringControl = raw.FindById(HexControlId);
            stringControl["_id"].IsString.Should().BeTrue();
            stringControl["Name"].AsString.Should().Be("string-control");
            typed.FindById(HexControlId).Name.Should().Be("string-control");

            var objectControl = raw.FindById(objectControlId);
            objectControl["_id"].IsObjectId.Should().BeTrue();
            objectControl["Name"].AsString.Should().Be("object-control");
            raw.FindById("ordinary-control")["Name"].AsString.Should().Be("ordinary-control");
        }

        private static void AssertDocumentedRejection(Exception failure)
        {
            failure.Should().BeOfType<LiteException>(
                "an unsupported String/ObjectId auto-id mapping needs a stable LiteDB diagnostic");
            var error = failure as LiteException;
            if (error == null) return;

            error.ErrorCode.Should().Be(LiteException.DATA_TYPE_NOT_ASSIGNABLE);
            error.Message.Should().Be(
                "Data type System.String is not assignable from data type LiteDB.ObjectId");
        }

        private static BsonDocument[] ReadRows(string filename)
        {
            using var observer = new LiteDatabase(filename);
            return ReadRows(observer);
        }

        private static BsonDocument[] ReadRows(ILiteDatabase database)
        {
            return database.GetCollection("rows").FindAll()
                .OrderBy(row => row["Name"].AsString)
                .ToArray();
        }

        private static string[] Fingerprints(IEnumerable<BsonDocument> rows)
        {
            return rows.Select(row => $"{row["_id"].Type}:{row["_id"]}|{row["Name"]}")
                .OrderBy(value => value)
                .ToArray();
        }

        private sealed class WriteResult
        {
            public int Affected { get; set; }
            public List<BsonValue> ReturnedIds { get; } = new List<BsonValue>();
        }
    }
}
