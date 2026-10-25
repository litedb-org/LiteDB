#if !NETFRAMEWORK
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Real Direct, Shared and Coordinated openers race a rebuild owner in this
    /// process. The owner pauses at a phase; the opener is observed blocked in the
    /// admission wait (not inferred from elapsed time) before the owner resumes.
    /// </summary>
    [Collection(CoordinatorPlatform_Tests.Collection)]
    public class RebuildOwnershipModes_Tests
    {
        private const string Handoff = "handoff";

        public static IEnumerable<object[]> ServiceCases()
        {
            foreach (var phase in new[] { "after-rebuild-source-claim", "after-temp-install", "after-rebuild-data-close" })
            foreach (var mode in new[] { "direct", "shared", "coordinated" })
            foreach (var encrypted in new[] { false, true })
                yield return new object[] { phase, mode, encrypted };
        }

        [Theory]
        [MemberData(nameof(ServiceCases))]
        public void Openers_in_every_mode_wait_for_the_rebuild_owner_and_then_write(string phase, string mode, bool encrypted)
        {
            var password = encrypted ? "password" : null;
            using var file = RebuildOwnership_Tests.Seed(password);
            var data = File.ReadAllBytes(file.Filename);
            var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
            // A Shared opener checks the marker before entering admission. While the
            // marker guards an installation it must refuse instead of waiting.
            var refuses = mode == "shared" && phase == "after-temp-install";
            Opener opener;

            using (var owner = new PausedOwner(phase, () =>
                new RebuildService(new EngineSettings { Filename = file.Filename, Password = password })
                    .Rebuild(new RebuildOptions())))
            {
                owner.AwaitPause();
                opener = new Opener(mode, file.Filename, password, write: true);
                if (refuses)
                {
                    opener.Join();
                    opener.Failure.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                    opener.Admitting.IsSet.Should().BeFalse();
                }
                else
                {
                    opener.AwaitBlockedInAdmission();
                    ProbeAdmissionIsHeld(file.Filename);
                }
                owner.Resume();
                opener.Join();
                owner.Join();
                opener.OpenedAfter(owner).Should().BeTrue();
            }

            if (refuses)
            {
                opener = new Opener(mode, file.Filename, password, write: true);
                opener.Join();
            }
            opener.Failure.Should().BeNull();
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(data);
            File.ReadAllBytes(FileHelper.GetSuffixFile(FileHelper.GetLogFile(file.Filename), "-backup", false)).Should().Equal(log);
            File.Exists(RebuildRecovery.GetMarkerFilename(file.Filename)).Should().BeFalse();
            RebuildOwnership_Tests.VerifyAndWrite(file.Filename, password);
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
            db.GetCollection("rows").FindById(4)["value"].AsString.Should().Be("contender", "the waiting opener's write was acknowledged");
        }

        [Theory]
        [InlineData("before-recovery-marker", false)]
        [InlineData("after-temp-install", false)]
        [InlineData(Handoff, false)]
        [InlineData(Handoff, true)]
        public void Shared_rebuild_holds_admission_through_installation_and_reopen(string phase, bool encrypted)
        {
            // Shared rebuilds take no exclusive file handles: admission alone keeps
            // a Direct opener from reading the pair while it is being replaced.
            var password = encrypted ? "password" : null;
            using var file = RebuildOwnership_Tests.Seed(password);
            var data = File.ReadAllBytes(file.Filename);
            var log = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
            PausedOwner owner = null;
            Opener opener;
            var ownerSettings = new EngineSettings { Filename = file.Filename, Password = password };
            // Handoff: the marker is gone and the files are installed; the owner
            // is about to reopen them while still holding admission.
            ownerSettings.BeforeOpeningAdmission = () => owner?.Reached(Handoff);
            using (owner = new PausedOwner(phase, () =>
            {
                using var db = new LiteDatabase(new SharedEngine(ownerSettings));
                return db.Rebuild();
            }))
            {
                owner.AwaitPause();
                opener = new Opener("direct", file.Filename, password, write: false);
                opener.AwaitBlockedInAdmission();
                ProbeAdmissionIsHeld(file.Filename);
                owner.Resume();
                opener.Join();
                owner.Join();
            }
            opener.Failure.Should().BeNull();
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(data);
            File.ReadAllBytes(FileHelper.GetSuffixFile(FileHelper.GetLogFile(file.Filename), "-backup", false)).Should().Equal(log);
            using (var shared = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Password = password, Connection = ConnectionType.Shared
            }))
                shared.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 4, ["value"] = "contender" });
            RebuildOwnership_Tests.VerifyAndWrite(file.Filename, password);
        }

        private static void ProbeAdmissionIsHeld(string filename)
        {
            var alias = Path.Combine(Path.GetDirectoryName(filename), ".", Path.GetFileName(filename));
            Action enter = () => RebuildAdmission.Enter(new EngineSettings { Filename = alias }, 0)?.Dispose();
            enter.Should().Throw<IOException>().WithMessage("Timed out waiting*");
        }

        /// <summary>Runs a rebuild on its own thread and parks it at one phase.</summary>
        private sealed class PausedOwner : IDisposable
        {
            private readonly string _phase;
            private readonly ManualResetEventSlim _paused = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _resume = new ManualResetEventSlim();
            private readonly Thread _thread;
            private bool _markerDeleted;
            internal Exception Failure { get; private set; }
            internal long Resumed { get; private set; }

            internal PausedOwner(string phase, Func<long> rebuild)
            {
                _phase = phase;
                _thread = new Thread(() =>
                {
                    try { rebuild(); }
                    catch (Exception ex) { Failure = ex; }
                    finally { _paused.Set(); }
                }) { IsBackground = true };
                RebuildService.SimulateInstallFailure = RebuildService.SimulateOwnershipFailure = this.Reached;
                _thread.Start();
            }

            internal void Reached(string point)
            {
                if (Thread.CurrentThread != _thread) return;
                if (point == "before-recovery-marker-delete") _markerDeleted = true;
                if (point != _phase || (point == Handoff && !_markerDeleted)) return;
                _paused.Set();
                _resume.Wait(TimeSpan.FromSeconds(25)).Should().BeTrue("the test resumes the owner");
                Resumed = Stopwatch();
            }

            internal void AwaitPause()
            {
                _paused.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
                Failure.Should().BeNull("the owner must reach " + _phase);
                _thread.IsAlive.Should().BeTrue("the owner must be parked at " + _phase);
            }

            internal void Resume() => _resume.Set();

            internal void Join()
            {
                _thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
                Failure.Should().BeNull();
                Resumed.Should().BePositive("the owner passed " + _phase);
            }

            public void Dispose()
            {
                _resume.Set();
                _thread.Join(TimeSpan.FromSeconds(20));
                RebuildService.SimulateInstallFailure = RebuildService.SimulateOwnershipFailure = null;
            }
        }

        /// <summary>Opens the database in one connection mode on its own thread.</summary>
        private sealed class Opener
        {
            private readonly Thread _thread;
            internal ManualResetEventSlim Admitting { get; } = new ManualResetEventSlim();
            internal Exception Failure { get; private set; }
            internal long Opened { get; private set; }

            internal Opener(string mode, string filename, string password, bool write)
            {
                var settings = new EngineSettings
                {
                    Filename = filename, Password = password, ReadOnly = !write,
                    BeforeOpeningAdmission = () => Admitting.Set()
                };
                _thread = new Thread(() =>
                {
                    try
                    {
                        using var db = new LiteDatabase(mode == "direct" ? new LiteEngine(settings) :
                            mode == "shared" ? new SharedEngine(settings) : (ILiteEngine)new CoordinatedEngine(settings));
                        var rows = db.GetCollection("rows");
                        rows.FindById(1)["value"].AsString.Should().Be("original");
                        rows.FindById(2)["value"].AsString.Should().Be("wal");
                        db.GetCollection("unrelated").FindById(1)["value"].AsString.Should().Be("preserved");
                        Opened = Stopwatch();
                        if (write) rows.Insert(new BsonDocument { ["_id"] = 4, ["value"] = "contender" });
                    }
                    catch (Exception ex) { Failure = ex; }
                }) { IsBackground = true };
                _thread.Start();
            }

            /// <summary>The opener announced admission and its thread is now blocked waiting.</summary>
            internal void AwaitBlockedInAdmission()
            {
                Admitting.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue("the opener reaches admission");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while ((_thread.ThreadState & ThreadState.WaitSleepJoin) == 0 && _thread.IsAlive && DateTime.UtcNow < deadline)
                    Thread.Yield();
                _thread.IsAlive.Should().BeTrue("admission must not pass while the owner holds it: " + Failure);
                (_thread.ThreadState & ThreadState.WaitSleepJoin).Should().NotBe(0);
                Opened.Should().Be(0);
            }

            internal void Join() => _thread.Join(TimeSpan.FromSeconds(25)).Should().BeTrue();

            internal bool OpenedAfter(PausedOwner owner) => Failure != null || Opened > owner.Resumed;
        }

        private static long Stopwatch() => System.Diagnostics.Stopwatch.GetTimestamp();
    }
}
#endif
