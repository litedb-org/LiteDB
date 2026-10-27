using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Tests.Database;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    [Collection(GlobalMapperCollection.Name)]
    public class AttributeMappingClone_Tests
    {
        public enum Registration
        {
            IdType, IgnoreType, IdName, IgnoreName, DataAnnotations, Unregistered
        }

        public enum MapperPath
        {
            ColdGlobal, WarmGlobal, Explicit
        }

        public static IEnumerable<object[]> Cases()
        {
            foreach (Registration registration in Enum.GetValues(typeof(Registration)))
            foreach (MapperPath path in Enum.GetValues(typeof(MapperPath)))
            {
                yield return new object[] { registration, path };
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Registered_attributes_survive_database_persistence(Registration registration, MapperPath path)
        {
            var mapper = new BsonMapper();
            switch (registration)
            {
                case Registration.IdType: mapper.RegisterIdAttribute(typeof(CustomIdAttribute)); break;
                case Registration.IgnoreType: mapper.RegisterIgnoreAttribute(typeof(CustomIgnoreAttribute)); break;
                case Registration.IdName: mapper.RegisterIdAttributeByName(typeof(CustomIdAttribute).FullName); break;
                case Registration.IgnoreName: mapper.RegisterIgnoreAttributeByName(typeof(CustomIgnoreAttribute).FullName); break;
                case Registration.DataAnnotations: mapper.UseDataAnnotations(); break;
            }

            var usesKey = registration == Registration.IdType || registration == Registration.IdName ||
                registration == Registration.DataAnnotations;
            var ignoresSecret = registration == Registration.IgnoreType || registration == Registration.IgnoreName ||
                registration == Registration.DataAnnotations;
            var expected = new BsonDocument { ["_id"] = usesKey ? 42 : 7, ["Name"] = "keep" };
            expected[usesKey ? "Id" : "Key"] = usesKey ? 7 : 42;
            if (!ignoresSecret) expected["Secret"] = "private";

            AssertPersistence(mapper, path, new RegisteredRow
            {
                Id = 7, Key = 42, Secret = "private", Name = "keep"
            }, expected);
        }

        [Theory]
        [InlineData(MapperPath.ColdGlobal, false)]
        [InlineData(MapperPath.WarmGlobal, false)]
        [InlineData(MapperPath.Explicit, false)]
        [InlineData(MapperPath.ColdGlobal, true)]
        [InlineData(MapperPath.WarmGlobal, true)]
        [InlineData(MapperPath.Explicit, true)]
        public void Built_in_and_fluent_mappings_keep_their_persisted_contract(MapperPath path, bool fluent)
        {
            var mapper = new BsonMapper();
            var expected = new BsonDocument { ["_id"] = 42, ["Name"] = "keep" };
            if (fluent)
            {
                mapper.Entity<RegisteredRow>().Id(x => x.Key, false).Ignore(x => x.Secret).Ignore(x => x.Id);
                AssertPersistence(mapper, path, new RegisteredRow { Key = 42, Secret = "private", Name = "keep" }, expected);
            }
            else
            {
                AssertPersistence(mapper, path, new BuiltInRow { Key = 42, Secret = "private", Name = "keep" }, expected);
            }
        }

        private static void AssertPersistence<T>(BsonMapper mapper, MapperPath path, T row, BsonDocument expected)
        {
            var original = BsonMapper.Global;
            using var file = new TempFile();
            try
            {
                // ColdGlobal reaches Clone before this entity has ever been mapped.
                if (path == MapperPath.WarmGlobal) mapper.ToDocument(row);
                BsonMapper.Global = mapper;
                using (var database = new LiteDatabase(file.Filename, path == MapperPath.Explicit ? mapper : null))
                {
                    if (path == MapperPath.Explicit) database.Mapper.Should().BeSameAs(mapper);
                    else database.Mapper.Should().NotBeSameAs(mapper);

                    database.GetCollection<T>("rows").Insert(row);
                    database.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 99, ["Value"] = "preserved" });
                }

                // A fresh explicit mapper reads raw persisted BSON, independent of the writer's caches.
                using var reopened = new LiteDatabase(file.Filename, new BsonMapper());
                var documents = reopened.GetCollection("rows").FindAll();
                using (new AssertionScope())
                {
                    var actual = documents.Should().ContainSingle().Which;
                    ((IDictionary<string, BsonValue>)actual).Should().Equal(expected);
                    Assert.NotNull(reopened.GetCollection("rows").FindById(expected["_id"]));
                    reopened.GetCollection("unrelated").FindById(99)["Value"].AsString.Should().Be("preserved");
                }
            }
            finally
            {
                BsonMapper.Global = original;
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Later_registrations_are_isolated_in_both_directions(bool byName)
        {
            var original = BsonMapper.Global;
            var global = new BsonMapper();
            Register(global, byName, typeof(CustomIdAttribute), typeof(CustomIgnoreAttribute));
            BsonMapper.Global = global;
            try
            {
                using var first = new LiteDatabase(":memory:");
                Register(global, byName, typeof(LaterIdAttribute), typeof(LaterIgnoreAttribute));
                using var second = new LiteDatabase(":memory:");

                var initial = first.Mapper.ToDocument(new RegisteredRow { Key = 42, Secret = "private" });
                initial["_id"].Should().Be(42);
                initial.ContainsKey("Secret").Should().BeFalse();
                AssertLaterMapping(first.Mapper, false);
                AssertLaterMapping(second.Mapper, true);

                // Registration on the first clone must neither replace nor mutate the source arrays.
                Register(first.Mapper, byName, typeof(CloneIdAttribute), typeof(CloneIgnoreAttribute));
                var row = new CloneRow { Id = 7, Key = 42, Secret = "private" };
                var local = first.Mapper.ToDocument(row);
                local["_id"].Should().Be(42);
                local.ContainsKey("Secret").Should().BeFalse();
                using var third = new LiteDatabase(":memory:");
                foreach (var mapper in new[] { global, second.Mapper, third.Mapper })
                {
                    var document = mapper.ToDocument(row);
                    document["_id"].Should().Be(7);
                    document["Secret"].Should().Be("private");
                }
            }
            finally
            {
                BsonMapper.Global = original;
            }
        }

        private static void Register(BsonMapper mapper, bool byName, Type id, Type ignore)
        {
            if (byName)
            {
                mapper.RegisterIdAttributeByName(id.FullName);
                mapper.RegisterIgnoreAttributeByName(ignore.FullName);
            }
            else
            {
                mapper.RegisterIdAttribute(id);
                mapper.RegisterIgnoreAttribute(ignore);
            }
        }

        private static void AssertLaterMapping(BsonMapper mapper, bool registered)
        {
            var document = mapper.ToDocument(new LaterRow { Id = 7, Key = 42, Secret = "private" });
            document["_id"].Should().Be(registered ? 42 : 7);
            document.ContainsKey("Secret").Should().Be(!registered);
        }

        public class RegisteredRow
        {
            public int Id { get; set; }
            [CustomId, Key]
            public int Key { get; set; }
            [CustomIgnore, NotMapped]
            public string Secret { get; set; }
            public string Name { get; set; }
        }

        public class BuiltInRow
        {
            [BsonId(false)]
            public int Key { get; set; }
            [BsonIgnore]
            public string Secret { get; set; }
            public string Name { get; set; }
        }

        public class LaterRow
        {
            public int Id { get; set; }
            [LaterId]
            public int Key { get; set; }
            [LaterIgnore]
            public string Secret { get; set; }
        }

        public class CloneRow
        {
            public int Id { get; set; }
            [CloneId]
            public int Key { get; set; }
            [CloneIgnore]
            public string Secret { get; set; }
        }

        public sealed class CustomIdAttribute : Attribute
        {
        }

        public sealed class CustomIgnoreAttribute : Attribute
        {
        }

        public sealed class LaterIdAttribute : Attribute
        {
        }

        public sealed class LaterIgnoreAttribute : Attribute
        {
        }

        public sealed class CloneIdAttribute : Attribute
        {
        }

        public sealed class CloneIgnoreAttribute : Attribute
        {
        }
    }
}
