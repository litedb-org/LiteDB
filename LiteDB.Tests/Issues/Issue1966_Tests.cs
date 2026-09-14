using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1966_Tests
    {
        [Theory]
        [InlineData(ConnectionType.Direct, false)]
        [InlineData(ConnectionType.Direct, true)]
        [InlineData(ConnectionType.Shared, false)]
        [InlineData(ConnectionType.Shared, true)]
        public void Readonly_index_creation_is_rejected_explicitly_and_existing_data_remains_readable(ConnectionType mode, bool existing)
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                setup.GetCollection("jobs").Insert(new BsonDocument { ["_id"] = 1, ["status"] = "created" });
                setup.GetCollection("jobs").EnsureIndex("status");
            }
            var before = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true, Connection = mode }))
            {
                var col = db.GetCollection("jobs");
                col.FindById(1)["status"].AsString.Should().Be("created");
                bool? created = null;
                var failure = Record.Exception(() => created = col.EnsureIndex(existing ? "status" : "missing_field"));
                if (failure == null)
                {
                    existing.Should().BeTrue("a readonly connection cannot create a new index");
                    created.Should().BeFalse();
                }
                else
                {
                    (failure is LiteException || failure is NotSupportedException).Should().BeTrue(
                        "a readonly operation must not masquerade as a missing WAL file: {0}", failure);
                }
            }
            File.ReadAllBytes(file.Filename).Should().Equal(before);
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("jobs").FindById(1)["status"].AsString.Should().Be("created");
            reopened.GetCollection("jobs").EnsureIndex("missing_field").Should().BeTrue();
        }
    }
}
