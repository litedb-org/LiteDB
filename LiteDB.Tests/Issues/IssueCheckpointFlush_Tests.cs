using System;
using System.IO;
using FluentAssertions;
using LiteDB;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class IssueCheckpointFlush_Tests
    {
        private class Entity
        {
            public int Id { get; set; }

            public string Value { get; set; } = string.Empty;
        }

        [Fact]
        public void CommittedChangesAreLostWhenClosingExternalStreamWithoutCheckpoint()
        {
            using var tempFile = new TempFile();

            using (var createStream = new FileStream(tempFile.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                using var createDb = new LiteDatabase(createStream);
                var collection = createDb.GetCollection<Entity>("entities");

                collection.Upsert(new Entity { Id = 1, Value = "initial" });

                createDb.Commit();
                createStream.Flush(true);
            }

            var updateStream = new FileStream(tempFile.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var updateDb = new LiteDatabase(updateStream);
            var updateCollection = updateDb.GetCollection<Entity>("entities");

            updateCollection.Upsert(new Entity { Id = 1, Value = "updated" });

            updateDb.Commit();
            updateStream.Flush(true);
            updateStream.Dispose();
            updateDb = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();

            using (var verifyStream = new FileStream(tempFile.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (var verifyDb = new LiteDatabase(verifyStream))
            {
                var document = verifyDb.GetCollection<Entity>("entities").FindById(1);

                document.Should().NotBeNull();
                document!.Value.Should().Be("updated");
            }
        }
    }
}
