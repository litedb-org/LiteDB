using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RebuildMarkerProbe_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-marker-probe-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "data.db");

        public RebuildMarkerProbe_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public void Missing_marker_does_not_throw_and_is_not_cached_across_probes()
        {
            var settings = new EngineSettings { Filename = Filename };
            var thread = Thread.CurrentThread;
            var missing = 0;
            EventHandler<FirstChanceExceptionEventArgs> observe = (_, args) =>
            {
                if (Thread.CurrentThread == thread && args.Exception is FileNotFoundException) missing++;
            };
            AppDomain.CurrentDomain.FirstChanceException += observe;
            try
            {
                for (var i = 0; i < 10; i++) RebuildRecovery.EnsureAvailable(settings);
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
            missing.Should().Be(0);

            var marker = RebuildRecovery.GetMarkerFilename(Filename);
            File.WriteAllText(marker, "incomplete installation");
            Action probe = () => RebuildRecovery.EnsureAvailable(settings);
            probe.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            File.ReadAllText(marker).Should().Be("incomplete installation");
            File.Exists(Filename).Should().BeFalse();
            File.Delete(marker);
            probe.Should().NotThrow();
            Directory.Delete(_directory);
            probe.Should().NotThrow();
        }

#if NET8_0_OR_GREATER
        [Fact]
        public void Dangling_marker_link_still_blocks_creation()
        {
            // Windows symlink creation requires permissions that ordinary CI users lack.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var marker = RebuildRecovery.GetMarkerFilename(Filename);
            File.CreateSymbolicLink(marker, Path.Combine(_directory, "missing-target"));
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { Filename = Filename }); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
            File.Exists(Filename).Should().BeFalse();
            File.Delete(marker);
        }

        [Fact]
        public void Inaccessible_parent_is_not_mistaken_for_an_absent_marker()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var marker = RebuildRecovery.GetMarkerFilename(Filename);
            File.WriteAllText(marker, "incomplete installation");
            var mode = File.GetUnixFileMode(_directory);
            try
            {
                File.SetUnixFileMode(_directory, UnixFileMode.None);
                // Root bypasses Unix permissions. Exercise the guard whenever the
                // filesystem enforces them, rather than asserting root cannot stat.
                try { File.GetAttributes(marker); return; }
                catch (UnauthorizedAccessException) { }
                Action probe = () => RebuildRecovery.EnsureAvailable(new EngineSettings { Filename = Filename });
                probe.Should().Throw<UnauthorizedAccessException>();
            }
            finally { File.SetUnixFileMode(_directory, mode); }
            File.ReadAllText(marker).Should().Be("incomplete installation");
            File.Exists(Filename).Should().BeFalse();
        }
#endif

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
