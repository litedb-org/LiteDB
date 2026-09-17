using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2798Reopen_Tests
    {
        [Fact]
        public void Fresh_mapper_uses_explicitly_known_concrete_schema_after_reopen()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
                db.GetCollection<Issue2798KnownMapping_Tests.IRecord>("rows").Insert(
                    new Issue2798KnownMapping_Tests.KeyRecord { Key = "saved", Number = 42 });
            var mapper = new BsonMapper();
            mapper.Entity<Issue2798KnownMapping_Tests.KeyRecord>();
            using var reopened = new LiteDatabase(file.Filename, mapper);
            reopened.GetCollection<Issue2798KnownMapping_Tests.IRecord>("rows")
                .Find(row => row.Key == "saved").Single().Number.Should().Be(42);
        }
    }
}
