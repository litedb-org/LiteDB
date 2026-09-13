using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2794_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public DateTime? Date { get; set; }
            public string Payload { get; set; }
            public Dictionary<string, int> Numbers { get; set; }
        }

        [Fact]
        public void Concurrent_size_changing_updates_preserve_document_block_chains_after_reopen()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<Row>("rows");
                col.Insert(Enumerable.Range(1, 16).Select(id => Create(id, 0)));
                Parallel.For(1, 17, id =>
                {
                    for (var round = 1; round <= 24; round++)
                    {
                        var expected = Create(id, round);
                        col.Update(expected).Should().BeTrue();
                        Check(col.FindById(id), expected);
                    }
                });
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection<Row>("rows").Count().Should().Be(16);
            for (var id = 1; id <= 16; id++) Check(reopened.GetCollection<Row>("rows").FindById(id), Create(id, 24));
        }

        private static Row Create(int id, int round)
        {
            return new Row
            {
                Id = id,
                Date = round % 2 == 0 ? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(id + round) : (DateTime?)null,
                Payload = new string((char)('A' + id), new[] { 100, 8100, 8200, 17000, 41000 }[round % 5]),
                Numbers = Enumerable.Range(0, round + 1).ToDictionary(x => "key" + x, x => id * 1000 + x)
            };
        }

        private static void Check(Row actual, Row expected)
        {
            actual.Id.Should().Be(expected.Id);
            (actual.Date.HasValue ? actual.Date.Value.ToUniversalTime() : (DateTime?)null).Should().Be(expected.Date);
            actual.Payload.Should().Be(expected.Payload);
            actual.Numbers.Should().BeEquivalentTo(expected.Numbers);
        }
    }
}
