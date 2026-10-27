#if !NETFRAMEWORK
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOneFileAdmission_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Shared_family_claim_allows_distinct_Shared_admission_but_excludes_read_only_Direct(bool mapped, bool readOnly)
        {
            using var temp = new TempFile();
            Seed(temp.Filename);
            var before = File.ReadAllBytes(temp.Filename);
            var settings = new EngineSettings { Filename = temp.Filename, SharedOneFile = mapped, ReadOnly = readOnly };
            using (var admission = NativeAdmission.Open(settings, shared: true))
            {
                // A distinct OFD must take the full Shared marker handshake,
                // rather than reusing this process's cached admission entry.
                using var peer = new DatabaseFileLock(settings.Filename, readOnly, false);
                peer.Admit(shared: true);
                using var direct = new DatabaseFileLock(settings.Filename, true, false);
                Action open = () => direct.Admit(shared: false);
                open.Should().Throw<IOException>();
            }
            Verify(temp.Filename, before);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Read_only_Direct_excludes_both_Shared_families_and_failed_admission_releases_every_lock(bool mapped, bool readOnly)
        {
            using var temp = new TempFile();
            Seed(temp.Filename);
            var before = File.ReadAllBytes(temp.Filename);
            var settings = new EngineSettings { Filename = temp.Filename, SharedOneFile = mapped, ReadOnly = readOnly };
            using (var direct = new DatabaseFileLock(temp.Filename, true, false))
            {
                direct.Admit(shared: false);
                Action open = () => { using var refused = NativeAdmission.Open(settings, shared: true); };
                open.Should().Throw<IOException>();
            }
            using (var admitted = NativeAdmission.Open(settings, shared: true)) { }
            // The opposite family must also open after final release.
            settings.SharedOneFile = !mapped;
            using (var admitted = NativeAdmission.Open(settings, shared: true)) { }
            Verify(temp.Filename, before);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Shared_families_refuse_a_read_only_marker_before_Direct_publishes_admission(bool mapped)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var temp = new TempFile();
            Seed(temp.Filename);
            var before = File.ReadAllBytes(temp.Filename);
            var filename = DatabaseFileIdentity.CanonicalPath(temp.Filename);
            using var marked = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var direct = new DatabaseFileLock(filename, true, false);
            var opening = Task.Run(() =>
            {
                var locks = 0;
                SharedCoordinationFile.CreationStage = (path, stage) =>
                {
                    if (path != filename || stage != "mode-shared-lock" || ++locks != 2) return;
                    marked.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                };
                try { direct.Admit(shared: false); }
                finally { SharedCoordinationFile.CreationStage = null; }
            });
            try
            {
                marked.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                var settings = new EngineSettings { Filename = filename, SharedOneFile = mapped };
                Action open = () => { using var refused = NativeAdmission.Open(settings, shared: true); };
                open.Should().Throw<IOException>().WithMessage("*read-only Direct*");
            }
            finally
            {
                release.Set();
                await opening;
            }
            using (var probe = new DatabaseFileLock(filename, true, false))
                probe.Conflicts(DatabaseFileLock.Admission).Should().BeTrue();
            direct.Dispose();
            using (var admitted = NativeAdmission.Open(new EngineSettings { Filename = filename, SharedOneFile = mapped }, true)) { }
            Verify(temp.Filename, before);
        }

        private static void Seed(string filename)
        {
            using var db = new LiteDatabase(filename);
            var docs = db.GetCollection("docs");
            docs.EnsureIndex("value");
            docs.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed", ["payload"] = new string('x', 9000) });
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 2, ["payload"] = "unrelated" });
            db.BeginTrans().Should().BeTrue();
            docs.Insert(new BsonDocument { ["_id"] = 3, ["value"] = "aborted" });
            db.Rollback().Should().BeTrue();
        }

        private static void Verify(string filename, byte[] before)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = filename, ReadOnly = true });
                var docs = db.GetCollection("docs");
                docs.Count().Should().Be(1);
                var doc = docs.FindOne(Query.EQ("value", "committed"));
                doc["_id"].AsInt32.Should().Be(1);
                doc["payload"].AsString.Should().Be(new string('x', 9000));
                Assert.Null(docs.FindById(3));
                db.GetCollection("sentinel").FindById(2)["payload"].AsString.Should().Be("unrelated");
            }
            File.ReadAllBytes(filename).Should().Equal(before);
        }
    }
}
#endif
