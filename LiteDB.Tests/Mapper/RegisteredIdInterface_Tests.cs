using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

using FluentAssertions;
using LiteDB.Tests.Database;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    [Collection(GlobalMapperCollection.Name)]
    public class RegisteredIdInterface_Tests
    {
        public enum Registration
        {
            IdType, IgnoreType, IdName, IgnoreName, DataAnnotations, Native, Fluent, Unregistered
        }

        public static IEnumerable<object[]> Cases()
        {
            foreach (Registration registration in Enum.GetValues(typeof(Registration)))
            foreach (var global in new[] { false, true })
            foreach (var warm in new[] { false, true })
            {
                yield return new object[] { registration, global, warm };
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Interface_queries_keep_their_contract_after_concrete_mapping(
            Registration registration, bool global, bool warm)
        {
            var mapper = new BsonMapper();
            switch (registration)
            {
                case Registration.IdType: mapper.RegisterIdAttribute(typeof(AliasAttribute)); break;
                case Registration.IgnoreType: mapper.RegisterIgnoreAttribute(typeof(IgnoreAttribute)); break;
                case Registration.IdName: mapper.RegisterIdAttributeByName(typeof(AliasAttribute).FullName); break;
                case Registration.IgnoreName: mapper.RegisterIgnoreAttributeByName(typeof(IgnoreAttribute).FullName); break;
                case Registration.DataAnnotations: mapper.UseDataAnnotations(); break;
                case Registration.Fluent:
                    mapper.Entity<IRegistered>().Id(x => x.Key, false);
                    mapper.Entity<RegisteredRow>().Id(x => x.Key, false);
                    break;
            }

            var original = BsonMapper.Global;
            using var file = new TempFile();
            try
            {
                // WarmGlobal clones cached mappings; ColdGlobal clones only registrations.
                if (warm) Warm(mapper);
                BsonMapper.Global = mapper;
                using (var database = new LiteDatabase(file.Filename, global ? null : mapper))
                {
                    database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 7, ["Key"] = 42 });
                    database.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 99, ["Value"] = "keep" });

                    var usesKeyAsId = registration == Registration.IdType || registration == Registration.IdName ||
                        registration == Registration.DataAnnotations || registration == Registration.Fluent;
                    var ignoresSecret = registration == Registration.IgnoreType || registration == Registration.IgnoreName ||
                        registration == Registration.DataAnnotations;
                    AssertQueries<IRegistered>(database, x => x.Key, x => x.Id, usesKeyAsId);
                    AssertQueries<INative>(database, x => x.Key, x => x.Id, true);

                    // Mapping an unrelated object must not change existing query meaning or stored data.
                    Warm(database.Mapper);
                    AssertQueries<IRegistered>(database, x => x.Key, x => x.Id, usesKeyAsId);
                    AssertQueries<INative>(database, x => x.Key, x => x.Id, true);
                    var document = database.Mapper.ToDocument(new RegisteredRow { Key = 42, Secret = "private" });
                    document["_id"].Should().Be(usesKeyAsId ? 42 : 7);
                    document.ContainsKey("Secret").Should().Be(!ignoresSecret);
                }

                using var reopened = new LiteDatabase(file.Filename, new BsonMapper());
                var stored = reopened.GetCollection("rows").FindById(7);
                ((IDictionary<string, BsonValue>)stored).Should().Equal(
                    new BsonDocument { ["_id"] = 7, ["Key"] = 42 });
                reopened.GetCollection("rows").Count().Should().Be(1);
                reopened.GetCollection("unrelated").FindById(99)["Value"].Should().Be("keep");
            }
            finally
            {
                BsonMapper.Global = original;
            }
        }

        private static void Warm(BsonMapper mapper)
        {
            mapper.ToDocument(new Implementation { Id = 7, Key = 42 });
        }

        private static void AssertQueries<T>(LiteDatabase database, Expression<Func<T, int>> key,
            Expression<Func<T, int>> id, bool keyIsId)
        {
            var collection = database.GetCollection<T>("rows");
            collection.Count(Equal(key, 7)).Should().Be(keyIsId ? 1 : 0);
            collection.Count(Equal(key, 42)).Should().Be(keyIsId ? 0 : 1);
            collection.Count(Equal(id, 7)).Should().Be(keyIsId ? 0 : 1);
            database.Mapper.GetExpression(key).Source.Should().Be(keyIsId ? "$._id" : "$.Key");
            // Exercise both ordinary predicates and an explicitly requested index on the same contract.
            collection.EnsureIndex(key);
            collection.Query().Where(Equal(key, keyIsId ? 7 : 42)).Select(key)
                .ToArray().Should().Equal(new[] { keyIsId ? 7 : 42 });
        }

        private static Expression<Func<T, bool>> Equal<T>(Expression<Func<T, int>> member, int value)
        {
            return Expression.Lambda<Func<T, bool>>(Expression.Equal(member.Body, Expression.Constant(value)), member.Parameters);
        }

        public interface IRegistered
        {
            [Alias, Key]
            int Key { get; }
            int Id { get; }
        }

        public interface INative
        {
            [BsonId(false)]
            int Key { get; }
            int Id { get; }
        }

        public class Implementation : IRegistered, INative
        {
            public int Key { get; set; }
            public int Id { get; set; }
        }

        public class RegisteredRow
        {
            public int Id { get; set; } = 7;
            [Alias, Key]
            public int Key { get; set; }
            [Ignore, NotMapped]
            public string Secret { get; set; }
        }

        public sealed class AliasAttribute : Attribute { }
        public sealed class IgnoreAttribute : Attribute { }
    }
}
