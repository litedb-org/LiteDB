using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1940_Tests
    {
        [Fact]
        public void OpeningDatabaseWithLegacyCorruptFreeListInWalShouldAutoHeal()
        {
            var tempDirectory = this.ExtractFixture();

            try
            {
                var databasePath = Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList.db");
                var logPath = Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList-log.db");

                Action firstOpen = () =>
                {
                    using var db = new LiteDatabase(databasePath);
                    var col = db.GetCollection<LegacyCorruptWalDoc>("verify");

                    col.EnsureIndex(x => x.Tags);
                    col.Insert(this.CreateDocs());

                    db.Checkpoint();
                };

                firstOpen.Should().NotThrow();

                if (File.Exists(logPath))
                {
                    new FileInfo(logPath).Length.Should().Be(0);
                }

                Action secondOpen = () =>
                {
                    using var db = new LiteDatabase(databasePath);
                    var col = db.GetCollection<LegacyCorruptWalDoc>("verify");

                    col.Insert(this.CreateDocs());
                };

                secondOpen.Should().NotThrow();
            }
            finally
            {
                this.DeleteTempDirectory(tempDirectory);
            }
        }

        [Fact]
        public void OpeningDatabaseWithoutAllocatingPagesShouldStillHealBeforeCheckpoint()
        {
            var tempDirectory = this.ExtractFixture();

            try
            {
                var databasePath = Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList.db");
                var logPath = Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList-log.db");

                Action openWithoutAllocations = () =>
                {
                    using var db = new LiteDatabase(databasePath);
                    db.GetCollectionNames().Should().NotBeNull();
                };

                openWithoutAllocations.Should().NotThrow();

                if (File.Exists(logPath))
                {
                    new FileInfo(logPath).Length.Should().Be(0);
                }

                Action reopenAndWrite = () =>
                {
                    using var db = new LiteDatabase(databasePath);
                    var col = db.GetCollection<LegacyCorruptWalDoc>("verify");

                    col.Insert(this.CreateDocs());
                };

                reopenAndWrite.Should().NotThrow();
            }
            finally
            {
                this.DeleteTempDirectory(tempDirectory);
            }
        }

        private string ExtractFixture()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), $"litedb-issue1940-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);

            ZipFile.ExtractToDirectory(
                Path.Combine(AppContext.BaseDirectory, "Resources", "Issue1940_CorruptFreeEmptyList.zip"),
                tempDirectory);

            var databasePath = Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList.db");
            var logPath = Path.Combine(tempDirectory, "Issue1940_CorruptFreeEmptyList-log.db");

            File.Exists(databasePath).Should().BeTrue();
            File.Exists(logPath).Should().BeTrue();

            return tempDirectory;
        }

        private void DeleteTempDirectory(string tempDirectory)
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }

        private IEnumerable<LegacyCorruptWalDoc> CreateDocs()
        {
            yield return new LegacyCorruptWalDoc
            {
                Number = 1,
                Payload = new string('a', 2048),
                Tags = new List<string> { "alpha", "beta" },
                Values = new List<string> { "one", "two" }
            };

            yield return new LegacyCorruptWalDoc
            {
                Number = 2,
                Payload = new string('b', 1536),
                Tags = new List<string> { "gamma", "delta" },
                Values = new List<string> { "three", "four" }
            };
        }

        private class LegacyCorruptWalDoc
        {
            public ObjectId Id { get; set; } = ObjectId.NewObjectId();

            public int Number { get; set; }

            public string Payload { get; set; } = string.Empty;

            public List<string> Tags { get; set; } = new List<string>();

            public List<string> Values { get; set; } = new List<string>();
        }
    }
}
