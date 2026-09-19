using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2805_Uniqueness_Tests
    {
        public class User
        {
            public int Id { get; set; }
            public string Email { get; set; }
        }

        [Fact]
        public void Unique_request_creates_the_canonical_index_when_only_a_non_unique_one_exists_under_another_name()
        {
            using var db = new LiteDatabase(":memory:");
            var users = db.GetCollection<User>("users");
            users.Insert(new User { Id = 1, Email = "a@x" });
            users.EnsureIndex("legacy", x => x.Email).Should().BeTrue();

            users.EnsureIndex(x => x.Email, unique: true).Should().BeTrue();

            ReadCatalog(db, "users").Should().BeEquivalentTo("_id:$._id:True", "legacy:$.Email:False", "Email:$.Email:True");
            var duplicate = Assert.Throws<LiteException>(() => users.Insert(new User { Id = 2, Email = "a@x" }));
            duplicate.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
            users.EnsureIndex(x => x.Email, unique: true).Should().BeFalse();
        }

        [Fact]
        public void Unique_request_throws_when_the_canonical_name_holds_a_non_unique_index()
        {
            using var db = new LiteDatabase(":memory:");
            var users = db.GetCollection<User>("users");
            users.EnsureIndex(x => x.Email).Should().BeTrue();

            var conflict = Assert.Throws<LiteException>(() => users.EnsureIndex(x => x.Email, unique: true));

            conflict.ErrorCode.Should().Be(LiteException.INDEX_ALREADY_EXIST);
            conflict.Message.Should().Contain("'Email'").And.Contain("not unique");
            Assert.Throws<LiteException>(() => users.EnsureIndex("$.Email", unique: true))
                .ErrorCode.Should().Be(LiteException.INDEX_ALREADY_EXIST);
            ReadCatalog(db, "users").Should().BeEquivalentTo("_id:$._id:True", "Email:$.Email:False");

            users.DropIndex("Email").Should().BeTrue();
            users.EnsureIndex(x => x.Email, unique: true).Should().BeTrue();
            ReadCatalog(db, "users").Should().BeEquivalentTo("_id:$._id:True", "Email:$.Email:True");
        }

        [Theory]
        [InlineData("legacy")]
        [InlineData("Email")]
        public void Non_unique_request_reuses_an_existing_unique_index(string name)
        {
            using var db = new LiteDatabase(":memory:");
            var users = db.GetCollection<User>("users");
            users.EnsureIndex(name, x => x.Email, unique: true).Should().BeTrue();

            users.EnsureIndex(x => x.Email).Should().BeFalse();
            users.EnsureIndex("$.Email").Should().BeFalse();

            ReadCatalog(db, "users").Should().BeEquivalentTo("_id:$._id:True", name + ":$.Email:True");
        }

        [Theory]
        [InlineData("legacy")]
        [InlineData("Email")]
        public void Unique_request_on_an_existing_unique_index_does_not_write(string name)
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                var users = setup.GetCollection<User>("users");
                users.Insert(new User { Id = 1, Email = "a@x" });
                users.EnsureIndex(name, x => x.Email, unique: true).Should().BeTrue();
            }
            var before = File.ReadAllBytes(file.Filename);
            var logFile = Path.Combine(Path.GetDirectoryName(file.Filename), Path.GetFileNameWithoutExtension(file.Filename) + "-log.db");

            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<User>("users").EnsureIndex(x => x.Email, unique: true).Should().BeFalse();

                (File.Exists(logFile) ? new FileInfo(logFile).Length : 0).Should().Be(0, "a matching index must not open a write transaction");
                ReadCatalog(db, "users").Should().BeEquivalentTo("_id:$._id:True", name + ":$.Email:True");
            }

            File.ReadAllBytes(file.Filename).Should().Equal(before);
        }

        [Fact]
        public void Unique_request_surfaces_duplicate_keys_and_leaves_no_half_created_index()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var users = db.GetCollection<User>("users");
                users.Insert(new[] { new User { Id = 1, Email = "a@x" }, new User { Id = 2, Email = "a@x" } });
                users.EnsureIndex("legacy", x => x.Email).Should().BeTrue();

                var duplicate = Assert.Throws<LiteException>(() => users.EnsureIndex(x => x.Email, unique: true));

                duplicate.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
                ReadCatalog(db, "users").Should().BeEquivalentTo("_id:$._id:True", "legacy:$.Email:False");
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var users = db.GetCollection<User>("users");
                ReadCatalog(db, "users").Should().BeEquivalentTo("_id:$._id:True", "legacy:$.Email:False");
                users.EnsureIndex(x => x.Email).Should().BeFalse();
                users.Find(x => x.Email == "a@x").Select(x => x.Id).Should().BeEquivalentTo(new[] { 1, 2 });
            }
        }

        [Fact]
        public void Read_only_database_reuses_a_unique_index_and_refuses_to_pretend_a_non_unique_one_is_unique()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                setup.GetCollection<User>("unique").EnsureIndex("legacy", x => x.Email, unique: true).Should().BeTrue();
                setup.GetCollection<User>("plain").EnsureIndex("legacy", x => x.Email).Should().BeTrue();
            }

            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true });

            db.GetCollection<User>("unique").EnsureIndex(x => x.Email, unique: true).Should().BeFalse();
            db.GetCollection<User>("plain").EnsureIndex(x => x.Email).Should().BeFalse();
            Assert.Throws<NotSupportedException>(() => db.GetCollection<User>("plain").EnsureIndex(x => x.Email, unique: true));
        }

        [Fact]
        public void Scalar_request_is_not_satisfied_by_a_vector_index_under_another_name()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["Tag"] = new BsonArray { 1f, 0f } });
            ILiteCollection<BsonDocument> vectors = rows;
            vectors.EnsureIndex("legacy", BsonExpression.Create("$.Tag"), new VectorIndexOptions(2)).Should().BeTrue();

            rows.EnsureIndex("$.Tag").Should().BeTrue();

            ReadCatalog(db, "rows").Should().BeEquivalentTo("_id:$._id:True", "legacy:$.Tag:False", "Tag:$.Tag:False");
            rows.EnsureIndex("$.Tag").Should().BeFalse();
        }

        [Fact]
        public void Vector_request_is_not_blocked_by_a_scalar_index_under_another_name()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var target = new[] { 1f, 0f };
            rows.Insert(new BsonDocument { ["_id"] = 1, ["Tag"] = new BsonArray { 1f, 0f } });
            rows.EnsureIndex("legacy", "$.Tag").Should().BeTrue();
            ILiteCollection<BsonDocument> vectors = rows;
            var options = new VectorIndexOptions(2);

            vectors.EnsureIndex(BsonExpression.Create("$.Tag"), options).Should().BeTrue();

            ReadCatalog(db, "rows").Should().BeEquivalentTo("_id:$._id:True", "legacy:$.Tag:False", "Tag:$.Tag:False");
            vectors.EnsureIndex(BsonExpression.Create("$.Tag"), options).Should().BeFalse();
            var plan = vectors.Query().WhereNear(BsonExpression.Create("$.Tag"), target, 0.001).GetPlan();
            plan["index"]["name"].AsString.Should().Be("Tag");
        }

        private static string[] ReadCatalog(LiteDatabase db, string collection)
        {
            return db.GetCollection("$indexes")
                .Find(Query.EQ("collection", collection))
                .Select(x => $"{x["name"].AsString}:{x["expression"].AsString}:{x["unique"].AsBoolean}")
                .ToArray();
        }
    }
}
