#if !NETFRAMEWORK
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class DirectHostReadOnlyRace_Tests
    {
        [Fact]
        public async Task Shared_cannot_publish_between_read_only_marker_and_admission()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var temp = new TempFile();
            using (var seed = new LiteDatabase(temp.Filename))
                seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "preserved" });
            var filename = DatabaseFileIdentity.CanonicalPath(temp.Filename);
            var before = File.ReadAllBytes(filename);
            using var marked = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var reader = new DatabaseFileLock(filename, true, false);
            var locks = 0;
            Action<string, string> observe = (path, stage) =>
            {
                if (path != filename || stage != "mode-shared-lock") return;
                if (++locks != 2) return;
                marked.Set(); // Marker acquired, admission not yet acquired.
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            };
            Task opening = null;
            try
            {
                opening = Task.Run(() =>
                {
                    SharedCoordinationFile.CreationStage = observe; // Observer is thread-local.
                    try { reader.Admit(shared: false); }
                    finally { SharedCoordinationFile.CreationStage = null; }
                });
                marked.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                using (var shared = new DatabaseFileLock(filename, false, false))
                {
                    Action admit = () => shared.Admit(shared: true);
                    admit.Should().Throw<IOException>().WithMessage("*read-only Direct*");
                }
                release.Set();
                await opening;
                using var probe = new DatabaseFileLock(filename, true, false);
                probe.Conflicts(DatabaseFileLock.Admission).Should().BeTrue();
            }
            finally
            {
                release.Set();
                if (opening != null) await opening;
            }
            reader.Dispose();
            using (var fresh = new DatabaseFileLock(filename, false, false)) fresh.Admit(shared: true);
            using var reopened = new LiteDatabase(temp.Filename);
            reopened.GetCollection("docs").Count().Should().Be(1);
            reopened.GetCollection("docs").FindById(1)["payload"].AsString.Should().Be("preserved");
            TempFile.ReadAllBytesShared(filename).Should().Equal(before);
        }
    }
}
#endif
