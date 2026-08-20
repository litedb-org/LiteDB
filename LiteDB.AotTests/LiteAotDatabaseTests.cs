using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    [TestClass]
    public sealed class LiteAotDatabaseTests
    {
        [TestMethod]
        public void GetCollection_WithoutExplicitMap_Throws()
        {
            var path = GetDatabasePath();

            try
            {
                using var database = new LiteAotDatabase(path, new BsonMapper());

                var exception = Assert.ThrowsException<InvalidOperationException>(() =>
                    database.GetCollection<AotTestRecord>("records"));

                StringAssert.Contains(exception.Message, typeof(AotTestRecord).FullName);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void RegisterAotEntityMapper_DuplicateType_Throws()
        {
            var mapper = new BsonMapper();
            mapper.RegisterAotEntityMapper(CreateRecordMap());

            Assert.ThrowsException<InvalidOperationException>(() =>
                mapper.RegisterAotEntityMapper(CreateRecordMap()));
        }

        private static readonly string[] expected = ["one", "two"];

        [TestMethod]
        public void GetCollection_WithExplicitMap_RoundTripsClosedListType()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new BsonMapper();
                mapper.RegisterAotEntityMapper(CreateRecordMap());

                using var database = new LiteAotDatabase(path, mapper);
                var collection = database.GetCollection<AotTestRecord>("records");
                collection.Insert(new AotTestRecord
                {
                    Name = "typed-aot",
                    Values = ["one", "two"]
                });

                var result = collection.FindById(1);

                Assert.IsNotNull(result);
                Assert.AreEqual("typed-aot", result.Name);
                CollectionAssert.AreEqual(expected, result.Values);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static EntityMapper CreateRecordMap()
        {
            var map = new EntityMapper(typeof(AotTestRecord))
            {
                CreateInstance = _ => new AotTestRecord()
            };

            map.Members.Add(new MemberMapper
            {
                AutoId = true,
                FieldName = "_id",
                MemberName = nameof(AotTestRecord.Id),
                DataType = typeof(int),
                UnderlyingType = typeof(int),
                Getter = entity => ((AotTestRecord)entity).Id,
                Setter = (entity, value) => ((AotTestRecord)entity).Id = (int)value
            });
            map.Members.Add(new MemberMapper
            {
                FieldName = nameof(AotTestRecord.Name),
                MemberName = nameof(AotTestRecord.Name),
                DataType = typeof(string),
                UnderlyingType = typeof(string),
                Getter = entity => ((AotTestRecord)entity).Name,
                Setter = (entity, value) => ((AotTestRecord)entity).Name = (string)value
            });
            map.Members.Add(new MemberMapper
            {
                FieldName = nameof(AotTestRecord.Values),
                MemberName = nameof(AotTestRecord.Values),
                DataType = typeof(List<string>),
                UnderlyingType = typeof(string),
                IsEnumerable = true,
                Getter = entity => ((AotTestRecord)entity).Values,
                Setter = (entity, value) => ((AotTestRecord)entity).Values = (List<string>)value,
                Serialize = (value, _) => SerializeStrings((List<string>)value),
                Deserialize = (value, _) => DeserializeStrings(value)
            });

            return map;
        }

        private static BsonArray SerializeStrings(List<string> values)
        {
            var array = new BsonArray();

            foreach (var value in values)
            {
                array.Add(value);
            }

            return array;
        }

        private static List<string> DeserializeStrings(BsonValue value)
        {
            var values = new List<string>();

            foreach (var item in value.AsArray)
            {
                values.Add(item.AsString);
            }

            return values;
        }

        private static string GetDatabasePath()
        {
            return Path.Combine(Path.GetTempPath(), $"litedb-aot-test-{Guid.NewGuid():N}.db");
        }

        private sealed class AotTestRecord
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public List<string> Values { get; set; } = [];
        }
    }
}
