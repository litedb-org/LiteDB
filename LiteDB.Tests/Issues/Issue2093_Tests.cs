using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2093_Tests
    {
        public enum UserRole
        {
            Guest,
            Member
        }

        public class User
        {
            public int Id { get; set; }
            public bool Removed { get; set; }

            [BsonField("user_name")]
            public string Name { get; set; }

            public UserRole Role { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Fully_persisted_predicate_differentials_match_the_CLR_oracle(bool enumAsInteger)
        {
            var mapper = new BsonMapper { EnumAsInteger = enumAsInteger };
            var cutoff = new DateTime(2021, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            var users = CreateUsers(cutoff);

            using var db = new LiteDatabase(":memory:", mapper);
            db.UtcDate = true;
            var collection = db.GetCollection<User>("User");
            collection.Insert(users);

            var normalizedName = "alice";
            Expression<Func<User, bool>> predicate = user =>
                user.Removed == false &&
                user.Name.ToLowerInvariant() == normalizedName &&
                user.Role == UserRole.Member &&
                user.CreatedAt >= cutoff;

            var materialized = collection.FindAll().OrderBy(user => user.Id).ToArray();
            var expected = materialized.Where(predicate.Compile()).Select(user => user.Id).ToArray();
            var actual = collection.Find(predicate).Select(user => user.Id).OrderBy(id => id).ToArray();

            expected.Should().Equal(1, 6);
            actual.Should().Equal(expected);
            collection.Find(user => user.Removed).Select(user => user.Id).Should().Equal(2);

            var expression = mapper.GetExpression(predicate);
            expression.Source.Should().Contain("user_name");
            expression.Source.Should().Contain("LOWER");
            expression.Source.Should().Contain("Role");
            expression.Source.Should().Contain("CreatedAt");
        }

        [Theory(Skip = "Known bug #2093")]
        [InlineData(false)]
        [InlineData(true)]
        public void Find_matches_the_CLR_default_when_removed_is_absent_from_persisted_BSON(bool enumAsInteger)
        {
            using var file = new TempFile();
            var cutoff = new DateTime(2021, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            var storedRole = enumAsInteger
                ? new BsonValue((int)UserRole.Member)
                : new BsonValue(nameof(UserRole.Member));

            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.GetCollection("User").Insert(new BsonDocument
                {
                    ["_id"] = 41,
                    ["user_name"] = "ALICE",
                    ["Role"] = storedRole,
                    ["CreatedAt"] = cutoff.AddMinutes(1)
                });
            }

            var mapper = new BsonMapper { EnumAsInteger = enumAsInteger };
            using var db = new LiteDatabase(file.Filename, mapper);
            db.UtcDate = true;
            var raw = db.GetCollection("User").FindById(41);

            Assert.NotNull(raw);
            raw.ContainsKey("Removed").Should().BeFalse();
            raw["user_name"].AsString.Should().Be("ALICE");
            raw["Role"].Should().Be(storedRole);
            raw["CreatedAt"].IsDateTime.Should().BeTrue();

            var collection = db.GetCollection<User>("User");
            var loaded = collection.FindAll().Should().ContainSingle().Subject;
            loaded.Id.Should().Be(41);
            loaded.Removed.Should().BeFalse("an absent value-type field has the CLR default after materialization");

            var normalizedName = "alice";
            Expression<Func<User, bool>> predicate = user =>
                user.Removed == false &&
                user.Name.ToLowerInvariant() == normalizedName &&
                user.Role == UserRole.Member &&
                user.CreatedAt >= cutoff;
            var compiled = predicate.Compile();

            compiled(loaded).Should().BeTrue();
            collection.Find(user => user.Id == 41).Select(user => user.Id).Should().Equal(41);

            var withoutRemoved = collection.Find(user =>
                    user.Name.ToLowerInvariant() == normalizedName &&
                    user.Role == UserRole.Member &&
                    user.CreatedAt >= cutoff)
                .Select(user => user.Id);
            withoutRemoved.Should().Equal(new[] { 41 },
                "the renamed string, enum and date clauses are healthy controls");

            var expected = collection.FindAll().Where(compiled).Select(user => user.Id).ToArray();
            var actual = collection.Find(predicate).Select(user => user.Id).ToArray();

            expected.Should().Equal(41);
            actual.Should().Equal(expected);
        }

        private static User[] CreateUsers(DateTime cutoff)
        {
            return new[]
            {
                NewUser(1, false, "ALICE", UserRole.Member, cutoff.AddMinutes(1)),
                NewUser(2, true, "ALICE", UserRole.Member, cutoff.AddMinutes(1)),
                NewUser(3, false, "ALICE", UserRole.Guest, cutoff.AddMinutes(1)),
                NewUser(4, false, "ALICE", UserRole.Member, cutoff.AddMinutes(-1)),
                NewUser(5, false, "MALLORY", UserRole.Member, cutoff.AddMinutes(1)),
                NewUser(6, false, "alice", UserRole.Member, cutoff.AddMinutes(1))
            };
        }

        private static User NewUser(
            int id,
            bool removed,
            string name,
            UserRole role,
            DateTime createdAt)
        {
            return new User
            {
                Id = id,
                Removed = removed,
                Name = name,
                Role = role,
                CreatedAt = createdAt
            };
        }
    }
}
