using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    [Collection(NativeFileSyncCollection.Name)]
    public class Issue2614_InitializationCleanup_Tests
    {
        [Theory]
        [InlineData(5)] // EIO
        [InlineData(28)] // ENOSPC
        public void FailedInitialization_ReleasesFileHandle_AndAllowsRetry(int errno)
        {
            using var file = new TempFile();
            using var unrelated = new TempFile();
            File.WriteAllText(unrelated.Filename, "preserve me");
            var settings = new EngineSettings { Filename = file.Filename };
            var original = NativeFileSync.SimulateErrno;

            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var syncs = 0;
                    NativeFileSync.SimulateErrno = path =>
                    {
                        if (path != file.Filename) return 0;
                        syncs++;
                        return errno;
                    };

                    Action create = () =>
                    {
                        using var disk = new DiskService(settings, new EngineState(null, settings), new[] { 2 });
                    };
                    create.Should().Throw<FileSyncException>().Which.HResult.Should().Be(errno);
                    syncs.Should().Be(1, "the failure must occur while syncing the new header");
                    NativeFileSync.SimulateErrno = original;

                    // Check before any GC: a leaked writer must not be rescued by a finalizer.
                    using var exclusive = File.Open(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    exclusive.Length.Should().Be(Constants.PAGE_SIZE);
                    if (attempt < 2) exclusive.SetLength(0);
                }
            }
            finally
            {
                NativeFileSync.SimulateErrno = original;
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
            }
            using (var reopened = new LiteDatabase(file.Filename))
            {
                reopened.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("committed");
            }
            File.ReadAllText(unrelated.Filename).Should().Be("preserve me");
        }
    }
}
