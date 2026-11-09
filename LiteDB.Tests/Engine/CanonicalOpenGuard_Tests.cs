using System;
using System.IO;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CanonicalOpenGuard_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-canonical-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "data.db");

        public CanonicalOpenGuard_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public void Normalization_checks_the_canonical_marker_before_admission()
        {
            using (var db = new LiteDatabase(Filename)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            File.WriteAllText(RebuildRecovery.GetMarkerFilename(Filename), "incomplete");
            var settings = new EngineSettings { Filename = Filename, SharedOneFile = false };
            if (DatabaseFileIdentity.Windows) settings.Filename = Filename.ToUpperInvariant();
            Action normalize = () => NativeAdmission.Normalize(settings);
            normalize.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            File.ReadAllText(RebuildRecovery.GetMarkerFilename(Filename)).Should().Be("incomplete");
        }

#if !NETFRAMEWORK
        [Fact]
        public void Admission_rechecks_a_normalized_path_retargeted_to_a_guarded_file()
        {
            if (DatabaseFileIdentity.Windows) return; // Unix symlinks require no elevated runner privileges.
            using (var db = new LiteDatabase(Filename)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var settings = new EngineSettings { Filename = Filename, SharedOneFile = false };
            NativeAdmission.Normalize(settings);
            var target = Path.Combine(_directory, "target.db");
            File.Move(Filename, target);
            File.CreateSymbolicLink(Filename, target);
            File.WriteAllText(RebuildRecovery.GetMarkerFilename(target), "incomplete");
            Action admit = () => { using var admission = NativeAdmission.Open(settings, false, normalized: true); };
            admit.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            File.ReadAllText(RebuildRecovery.GetMarkerFilename(target)).Should().Be("incomplete");
            File.Delete(RebuildRecovery.GetMarkerFilename(target));
            using var reopened = new LiteDatabase(target);
            reopened.GetCollection("rows").FindById(1).Should().NotBeNull();
        }

        [Fact]
        public void Short_windows_canonical_probes_do_not_allocate_long_path_buffers()
        {
            if (!DatabaseFileIdentity.Windows) return;
            using (var db = new LiteDatabase(Filename)) { }
            var expected = DatabaseFileIdentity.CanonicalPath(Filename);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++) DatabaseFileIdentity.CanonicalPath(Filename).Should().Be(expected);
            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(256 * 1024);
        }
#endif

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
