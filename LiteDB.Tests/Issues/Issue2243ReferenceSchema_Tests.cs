using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2243ReferenceSchema_Tests
    {
        public interface ITarget { string Name { get; set; } }
        public class Target : ITarget { public string Name { get; set; } }
        public class Row
        {
            public int Id { get; set; }
            [BsonRef("targets")] public ITarget[] Targets { get; set; }
        }

        [Fact]
        public void Empty_and_null_entries_need_no_reference_identity_but_values_do()
        {
            var mapper = new BsonMapper { SerializeNullValues = true };
            Assert.True(mapper.ToDocument(new Row { Targets = null })["Targets"].IsNull);
            Assert.Equal(0, mapper.ToDocument(new Row { Targets = new ITarget[0] })["Targets"].AsArray.Count);
            Assert.Equal(0, mapper.ToDocument(new Row { Targets = new ITarget[] { null } })["Targets"].AsArray.Count);
            var failure = Assert.Throws<LiteException>(() => mapper.ToDocument(new Row { Targets = new ITarget[] { null, new Target() } }));
            Assert.Contains(typeof(ITarget).FullName, failure.Message);
            Assert.Contains("_id", failure.Message);
        }

        [Fact]
        public void Invalid_reference_later_in_insert_batch_rolls_back_earlier_rows()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var rows = db.GetCollection<Row>("rows");
            Assert.Throws<LiteException>(() => rows.Insert(new[]
            {
                new Row { Id = 1, Targets = new ITarget[0] },
                new Row { Id = 2, Targets = new ITarget[] { new Target() } }
            }));
            Assert.Equal(0, rows.Count());
            rows.Insert(new Row { Id = 3, Targets = new ITarget[0] });
            Assert.Equal(1, rows.Count());
        }
    }
}
