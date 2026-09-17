using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1376_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public Dictionary<string, object> Data { get; set; }
        }

        public class InterfaceRow
        {
            public int Id { get; set; }
            public IDictionary<string, object> Data { get; set; }
        }

        [Fact]
        public void ContainsKey_rejects_legacy_dictionary_values_stored_as_arrays()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<InterfaceRow>();
            db.GetCollection(col.Name).Insert(new BsonDocument
            {
                ["_id"] = 1,
                ["Data"] = new BsonArray { new BsonDocument { ["Key"] = "key", ["Value"] = BsonValue.Null } }
            });
            db.GetCollection(col.Name).FindById(1)["Data"].IsArray.Should().BeTrue();
            Action query = () => col.Find(x => x.Data.ContainsKey("key")).ToArray();
            query.Should().Throw<NotSupportedException>().WithMessage("*BSON document*");
        }

        [Fact]
        public void ContainsKey_rejects_null_and_non_string_runtime_keys()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>();
            col.Insert(new Row { Id = 1, Data = new Dictionary<string, object> { ["key"] = 42 } });
            string key = null;
            Action query = () => col.Find(x => x.Data.ContainsKey(key)).ToArray();
            query.Should().Throw<ArgumentNullException>();
            var raw = db.GetCollection(col.Name);
            Action invalidKey = () => raw.Find("CONTAINSKEY($.Data, 42) = true").ToArray();
            invalidKey.Should().Throw<NotSupportedException>().WithMessage("*string key*");
        }

        public class HiddenDictionary : Dictionary<string, object>
        {
            public new bool ContainsKey(string key) => false;
        }

        public class HiddenRow
        {
            public int Id { get; set; }
            public HiddenDictionary Data { get; set; }
        }

        [Fact]
        public void ContainsKey_does_not_replace_a_hidden_method_with_interface_semantics()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<HiddenRow>();
            var data = new HiddenDictionary { ["key"] = 42 };
            data.ContainsKey("key").Should().BeFalse();
            ((IDictionary<string, object>)data).ContainsKey("key").Should().BeTrue();
            col.Insert(new HiddenRow { Id = 1, Data = data });
            Action query = () => col.Find(x => x.Data.ContainsKey("key")).ToArray();
            query.Should().Throw<NotSupportedException>();
            col.Find(x => ((IDictionary<string, object>)x.Data).ContainsKey("key"))
                .Select(x => x.Id).Should().Equal(1);
        }

        public class DictionaryListRow
        {
            public int Id { get; set; }
            public List<Dictionary<string, object>> Dictionaries { get; set; }
        }

        [Fact]
        public void ContainsKey_supports_Any_and_All_including_null_values_and_empty_sequences()
        {
            var present = new Dictionary<string, object> { ["key"] = null };
            var absent = new Dictionary<string, object>();
            var rows = new[]
            {
                new DictionaryListRow { Id = 1, Dictionaries = new List<Dictionary<string, object>> { present, present } },
                new DictionaryListRow { Id = 2, Dictionaries = new List<Dictionary<string, object>> { present, absent } },
                new DictionaryListRow { Id = 3, Dictionaries = new List<Dictionary<string, object>> { absent } },
                new DictionaryListRow { Id = 4, Dictionaries = new List<Dictionary<string, object>>() }
            };
            using var db = new LiteDatabase(":memory:", new BsonMapper { SerializeNullValues = true });
            var col = db.GetCollection<DictionaryListRow>();
            col.Insert(rows);
            foreach (var key in new[] { "key", "missing" })
            {
                col.Find(x => x.Dictionaries.Any(d => d.ContainsKey(key))).Select(x => x.Id)
                    .Should().Equal(rows.Where(x => x.Dictionaries.Any(d => d.ContainsKey(key))).Select(x => x.Id));
                col.Find(x => x.Dictionaries.All(d => d.ContainsKey(key))).Select(x => x.Id)
                    .Should().Equal(rows.Where(x => x.Dictionaries.All(d => d.ContainsKey(key))).Select(x => x.Id));
                col.Find(x => x.Dictionaries.Any(d => !d.ContainsKey(key))).Select(x => x.Id)
                    .Should().Equal(rows.Where(x => x.Dictionaries.Any(d => !d.ContainsKey(key))).Select(x => x.Id));
            }
        }

        [Theory]
        [InlineData("en-US/Ordinal")]
        [InlineData("tr-TR/IgnoreCase")]
        public void ContainsKey_uses_stored_field_names_and_parameterized_keys(string collation)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation(collation) },
                new BsonMapper { SerializeNullValues = true });
            var col = db.GetCollection<Row>();
            var keys = new[] { "I", "a.b", "a' OR true", "with space", "ümlaut", "" };
            for (var i = 0; i < keys.Length; i++)
            {
                col.Insert(new Row { Id = i + 1, Data = new Dictionary<string, object> { [keys[i]] = null } });
            }
            foreach (var key in keys)
            {
                var expected = Array.IndexOf(keys, key) + 1;
                col.Find(x => x.Data.ContainsKey(key)).Select(x => x.Id).Should().Equal(expected);
                col.Find(x => ((IDictionary<string, object>)x.Data).ContainsKey(key)).Select(x => x.Id).Should().Equal(expected);
                col.Find(x => ((IReadOnlyDictionary<string, object>)x.Data).ContainsKey(key)).Select(x => x.Id).Should().Equal(expected);
            }
            col.Find(x => x.Data.ContainsKey("i")).Select(x => x.Id).Should().Equal(1);
            var documents = db.GetCollection(col.Name);
            documents.Find(x => x["Data"].AsDocument.ContainsKey("i")).Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        [Fact]
        public void ContainsKey_rejects_non_string_dictionary_keys_without_inventing_field_names()
        {
            var mapper = new BsonMapper();
            Action translate = () => mapper.GetExpression<Dictionary<int, object>, bool>(x => x.ContainsKey(1));
            translate.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void ContainsKey_distinguishes_absent_null_and_non_null_values()
        {
            var rows = new[]
            {
                new Row { Id = 1, Data = new Dictionary<string, object> { ["key"] = 42 } },
                new Row { Id = 2, Data = new Dictionary<string, object> { ["key"] = null } },
                new Row { Id = 3, Data = new Dictionary<string, object> { ["other"] = 42 } },
                new Row { Id = 4, Data = new Dictionary<string, object>() }
            };
            using var db = new LiteDatabase(":memory:", new BsonMapper { SerializeNullValues = true });
            var col = db.GetCollection<Row>();
            col.Insert(rows);
            foreach (var key in new[] { "key", "other", "missing" })
            {
                col.Find(x => x.Data.ContainsKey(key)).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(rows.Where(x => x.Data.ContainsKey(key)).Select(x => x.Id));
                col.Find(x => !x.Data.ContainsKey(key)).Select(x => x.Id).OrderBy(x => x)
                    .Should().Equal(rows.Where(x => !x.Data.ContainsKey(key)).Select(x => x.Id));
            }
        }
    }
}
