using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class AutoIdAssignment_Tests
    {
        public class CustomRow
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        public class ThrowingRow
        {
            public int? Id
            {
                get => null;
                set => throw new InvalidOperationException("ID setter failed");
            }
        }

        public class NullableRow<T> where T : struct
        {
            public T? Id { get; set; }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void Nullable_ids_generate_for_null_and_empty_and_survive_reopen(int path)
        {
            CheckNullableId<int>(path);
            CheckNullableId<long>(path);
            CheckNullableId<Guid>(path);
        }

        private static void CheckNullableId<T>(int path) where T : struct
        {
            using var file = new TempFile();
            var rows = new[] { new NullableRow<T>(), new NullableRow<T> { Id = default(T) } };
            using (var db = new LiteDatabase(file.Filename))
            {
                var collection = db.GetCollection<NullableRow<T>>("rows");
                foreach (var row in rows)
                {
                    Write(collection, row, path);
                    row.Id.HasValue.Should().BeTrue();
                    row.Id.Value.Should().NotBe(default(T));
                }
                rows[0].Id.Should().NotBe(rows[1].Id);
            }
            using var reopened = new LiteDatabase(file.Filename);
            var restored = reopened.GetCollection<NullableRow<T>>("rows");
            restored.Count().Should().Be(2);
            foreach (var row in rows)
            {
                var id = BsonMapper.Global.ToDocument(row)["_id"];
                restored.FindById(id).Id.Should().Be(row.Id);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void Custom_setter_can_copy_back_ObjectId_to_a_string_member(int path)
        {
            var mapper = new BsonMapper();
            mapper.ResolveMember = (type, info, member) =>
            {
                if (type != typeof(CustomRow) || member.MemberName != nameof(CustomRow.Id)) return;
                member.Serialize = (value, _) => string.IsNullOrEmpty((string)value)
                    ? new BsonValue(ObjectId.Empty) : new BsonValue(new ObjectId((string)value));
                member.Deserialize = (value, _) => value.AsObjectId;
                member.Setter = (target, value) => ((CustomRow)target).Id = ((ObjectId)value).ToString();
            };

            using var file = new TempFile();
            var row = new CustomRow { Id = "", Name = "custom" };
            using (var db = new LiteDatabase(file.Filename, mapper))
            {
                Write(db.GetCollection<CustomRow>("rows"), row, path);
                row.Id.Should().NotBeNullOrEmpty();
                db.GetCollection("rows").FindById(new ObjectId(row.Id))["Name"].AsString.Should().Be("custom");
            }
            using var reopened = new LiteDatabase(file.Filename, mapper);
            reopened.GetCollection<CustomRow>("rows").FindById(new ObjectId(row.Id)).Id.Should().Be(row.Id);
            reopened.GetCollection("rows").Count().Should().Be(1);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(3, false)]
        [InlineData(4, false)]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, true)]
        [InlineData(4, true)]
        public void Throwing_setter_rolls_back_before_caller_can_commit(int path, bool explicitTransaction)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 91, ["Name"] = "control" });
                if (explicitTransaction) db.BeginTrans();
                Action write = () => Write(db.GetCollection<ThrowingRow>("rows"), new ThrowingRow(), path);
                write.Should().Throw<InvalidOperationException>().WithMessage("ID setter failed");
                db.Commit().Should().BeFalse();
                db.GetCollection("rows").Count().Should().Be(1);
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(1);
            reopened.GetCollection("rows").FindById(91)["Name"].AsString.Should().Be("control");
        }

        private static void Write<T>(ILiteCollection<T> collection, T row, int path)
        {
            switch (path)
            {
                case 0: collection.Insert(row); break;
                case 1: collection.Insert(Lazy(row)); break;
                case 2: collection.Upsert(row); break;
                case 3: collection.Upsert(Lazy(row)); break;
                case 4: collection.InsertBulk(Lazy(row)); break;
                default: throw new ArgumentOutOfRangeException(nameof(path));
            }
        }

        private static IEnumerable<T> Lazy<T>(T row)
        {
            yield return row;
        }
    }
}
