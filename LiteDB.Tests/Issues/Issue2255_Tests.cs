using System.Collections.Generic;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2255_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public Dictionary<double, double> Values { get; set; }
        }

        [Theory]
        [InlineData("de-DE", "en-US")]
        [InlineData("en-US", "de-DE")]
        [InlineData("fr-FR", "tr-TR")]
        public void Dictionary_keys_are_invariant_across_write_update_and_read_cultures(string write, string read)
        {
            var culture = CultureInfo.CurrentCulture;
            using var file = new TempFile();
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(write);
                using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
                {
                    db.GetCollection<Row>("rows").Insert(new Row
                    {
                        Id = 1, Values = new Dictionary<double, double> { [9.9] = 1.23, [-0.125] = 7.5 }
                    });
                }
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(read);
                using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
                {
                    var raw = db.GetCollection("rows").FindById(1)["Values"].AsDocument;
                    raw.Keys.Should().BeEquivalentTo("9.9", "-0.125");
                    raw["9.9"].AsDouble.Should().Be(1.23);
                    var col = db.GetCollection<Row>("rows");
                    var row = col.FindById(1);
                    row.Values.Should().BeEquivalentTo(new Dictionary<double, double> { [9.9] = 1.23, [-0.125] = 7.5 });
                    row.Values[10.1] = 2.34;
                    col.Update(row).Should().BeTrue();
                }
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
                {
                    db.GetCollection<Row>("rows").FindById(1).Values.Should().BeEquivalentTo(
                        new Dictionary<double, double> { [9.9] = 1.23, [-0.125] = 7.5, [10.1] = 2.34 });
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }
    }
}
