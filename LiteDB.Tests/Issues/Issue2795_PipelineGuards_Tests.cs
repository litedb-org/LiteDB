using System.Linq;

using FluentAssertions;
using FluentAssertions.Execution;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2795_PipelineGuards_Tests
    {
        [Theory]
        [InlineData(0, false)]
        [InlineData(7, false)]
        [InlineData(39, false)]
        [InlineData(41, false)]
        [InlineData(50, false)]
        [InlineData(0, true)]
        [InlineData(7, true)]
        [InlineData(39, true)]
        [InlineData(41, true)]
        [InlineData(50, true)]
        public void Indexed_pages_match_the_external_ledger_after_reopen(int offset, bool descending)
        {
            using var file = new TempFile();
            var fixture = Enumerable.Range(1, 41).Select(CreateDocument).ToArray();
            using (var database = new LiteDatabase(file.Filename))
            {
                var collection = database.GetCollection<GuardDocument>("pages");
                collection.InsertBulk(fixture.OrderBy(document => (document.Id * 29) % 41))
                    .Should().Be(fixture.Length);
                collection.EnsureIndex(document => document.Rank).Should().BeTrue();
                database.Checkpoint();
            }

            using var reopened = new LiteDatabase(file.Filename);
            var rows = reopened.GetCollection<GuardDocument>("pages");
            var direction = descending ? Query.Descending : Query.Ascending;
            var expected = (descending
                    ? fixture.OrderByDescending(document => document.Rank)
                    : fixture.OrderBy(document => document.Rank))
                .Skip(offset).Take(5).Select(Signature).ToArray();

            using var assertions = new AssertionScope();
            rows.Query().OrderBy(document => document.Rank, direction).Skip(offset).Limit(5)
                .ToList().Select(Signature).Should().Equal(expected);
            rows.Query().OrderBy(document => document.Rank, direction).Offset(offset).Limit(5)
                .ToList().Select(Signature).Should().Equal(expected);
            rows.FindAll().OrderBy(document => document.Id).Select(Signature)
                .Should().Equal(fixture.Select(Signature), "pagination must leave every stored row intact");
        }

        [Fact]
        public void Offset_counts_rows_after_including_and_filtering_references()
        {
            using var database = new LiteDatabase(":memory:");
            var targets = Enumerable.Range(1, 41).Select(CreateDocument).ToArray();
            database.GetCollection<GuardDocument>("targets2795").InsertBulk(targets);
            var fixture = Enumerable.Range(1, 41).Select(id => new ReferenceRow
            {
                Id = id,
                Target = targets[(id * 17) % targets.Length]
            }).ToArray();
            var rows = database.GetCollection<ReferenceRow>("references");
            rows.InsertBulk(fixture);
            var query = rows.Query().Include(row => row.Target)
                .Where(row => row.Target.Keep).Offset(3).Limit(4);
            var actual = query.ToList();
            var expected = fixture.Where(row => row.Target.Keep).Skip(3).Take(4).ToArray();

            using var assertions = new AssertionScope();
            actual.Select(row => row.Id).Should().Equal(expected.Select(row => row.Id));
            actual.Select(row => Signature(row.Target))
                .Should().Equal(expected.Select(row => Signature(row.Target)));
            var plan = query.GetPlan();
            plan["filters"].AsArray.Count.Should().Be(1);
            plan["includeBefore"].AsArray.Select(value => value.AsString).Should().Equal("$.Target");
            // Prove that the data lives in another collection, not an inline copy that
            // accidentally makes the Include stage unnecessary.
            var raw = database.GetCollection("references").FindById(1)["Target"].AsDocument;
            raw["$ref"].AsString.Should().Be("targets2795");
            raw.ContainsKey("Keep").Should().BeFalse();
        }

        [Fact]
        public void Offset_stays_after_document_filter_and_unindexed_sort()
        {
            using var database = new LiteDatabase(":memory:");
            var collection = database.GetCollection<GuardDocument>("issue2795_guards");
            var fixture = Enumerable.Range(1, 41)
                .Select(CreateDocument)
                .OrderBy(document => (document.Id * 29) % 41)
                .ToArray();

            collection.InsertBulk(fixture);

            var filtered = collection.Query()
                .Where(document => document.Keep)
                .Skip(5)
                .Limit(4)
                .ToList();
            var sorted = collection.Query()
                .OrderBy(document => document.Rank)
                .Offset(7)
                .Limit(5)
                .ToList();

            var expectedFiltered = fixture
                .OrderBy(document => document.Id)
                .Where(document => document.Keep)
                .Skip(5)
                .Take(4)
                .Select(Signature);
            var expectedSorted = fixture
                .OrderBy(document => document.Rank)
                .Skip(7)
                .Take(5)
                .Select(Signature);

            var filterPlan = collection.Query()
                .Where(document => document.Keep)
                .Skip(5)
                .Limit(4)
                .GetPlan();
            var sortPlan = collection.Query()
                .OrderBy(document => document.Rank)
                .Offset(7)
                .Limit(5)
                .GetPlan();

            using var assertions = new AssertionScope();
            filtered.Select(Signature).Should().Equal(expectedFiltered,
                "Skip must count rows after the document predicate");
            sorted.Select(Signature).Should().Equal(expectedSorted,
                "Offset must count rows after an unresolved document sort");
            filterPlan["filters"].AsArray.Count.Should().Be(1);
            sortPlan["orderBy"].AsArray.Count.Should().Be(1);
            sortPlan["lookup"]["loader"].AsString.Should().Be("document");
        }

        private static GuardDocument CreateDocument(int id)
        {
            return new GuardDocument
            {
                Id = id,
                Keep = id % 3 == 1,
                Rank = (id * 17) % 43,
                Marker = $"guard-{id:D2}-{unchecked(id * 7919):x8}"
            };
        }

        private static string Signature(GuardDocument document)
        {
            return $"{document.Id}|{document.Keep}|{document.Rank}|{document.Marker}";
        }

        private sealed class GuardDocument
        {
            public int Id { get; set; }
            public bool Keep { get; set; }
            public int Rank { get; set; }
            public string Marker { get; set; }
        }

        private sealed class ReferenceRow
        {
            public int Id { get; set; }

            [BsonRef("targets2795")]
            public GuardDocument Target { get; set; }
        }
    }
}
