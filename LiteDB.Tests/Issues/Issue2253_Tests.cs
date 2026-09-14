using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2253_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public Dictionary<string, object> Properties { get; set; }
        }

        [Fact]
        public void Expando_inside_object_dictionary_roundtrips_every_key_and_value()
        {
            IDictionary<string, object> nested = new ExpandoObject();
            nested["prop1"] = "val1";
            nested["prop2"] = "val2";
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<Row>("rows").Insert(new Row
                {
                    Id = 7,
                    Properties = new Dictionary<string, object> { ["number"] = 42, ["nested"] = nested }
                });
            }
            using var reopened = new LiteDatabase(file.Filename);
            var row = reopened.GetCollection<Row>("rows").FindById(7);
            row.Properties["number"].Should().Be(42);
            // Accept either supported dictionary or KeyValuePair representation; values must survive.
            var sequence = row.Properties["nested"] as IEnumerable;
            sequence.Should().NotBeNull();
            var entries = sequence.Cast<object>().ToArray();
            entries.Should().HaveCount(2);
            entries.Should().AllBeOfType<KeyValuePair<string, object>>();
            entries.Cast<KeyValuePair<string, object>>().ToDictionary(x => x.Key, x => x.Value)
                .Should().BeEquivalentTo(nested);
            var raw = reopened.GetCollection("rows").FindById(7);
            var serialized = JsonSerializer.Serialize(raw);
            serialized.Should().Contain("prop1").And.Contain("val1").And.Contain("prop2").And.Contain("val2");
        }
    }
}
