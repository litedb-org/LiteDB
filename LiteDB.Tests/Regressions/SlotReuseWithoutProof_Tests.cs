#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Reusing a retired WAL slot, in the conservative baseline: every engine proves the data file and
    /// the log sync before its first reuse (DiskService.ProveSlotReuse). A slot is free only while the
    /// root that witnesses it names it: a retiring checkpoint syncs its witness records before the root
    /// and the root before its header journal goes, and a pending journal makes the next open write the
    /// header back and sync it (so a failed sync's pages marked clean still reach the device) or refuse
    /// the open. (Reuse without that proof, implementation note 15, is a later layer.) A clear that never reached
    /// the device leaves the old frame in a witnessed slot, which recovery skips, as it skips a torn new
    /// frame there. Each reusing commit is checked with the images a power loss may leave at every WAL
    /// write and around its sync (<see cref="ForgetfulFile"/>: its pending writes lost, written back, or
    /// the last one torn): each recovers the committed set exactly, documents and an index query, never
    /// losing a commit acknowledged durable, never showing one whose confirmation is not whole on the
    /// device, and never mixing in a slot's old frame.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class SlotReuseWithoutProof_Tests
    {
        private const int Rows = 64;
        private const string AtRootPublication = "retirement-before-header-write";
        private static readonly ImageKind[] Kinds = { ImageKind.Lost, ImageKind.WrittenBack, ImageKind.Torn };

        /// <summary>
        /// A retiring checkpoint published its root durably and cleared the slots it retired. Its
        /// clears reached the device ("synced"); or their sync failed with EIO and forgot them
        /// ("forgotten": the device keeps the old frames, the cache shows cleared slots that no later
        /// sync writes); or the process died right before that sync ("pending": the clears stay dirty in
        /// the cache). A fresh engine of a new process reuses those slots without syncing anything for
        /// them first (for "pending" one that opted out of durable commits: its first commit syncs
        /// nothing, so the clears may reach the device before, after or without its frames). A power
        /// loss at any WAL write of its commit, or after it, recovers exactly the committed set, plain
        /// and encrypted; so does one after a durable commit of the next engine.
        /// </summary>
        [Theory]
        [InlineData(null, "synced")]
        [InlineData(null, "forgotten")]
        [InlineData(null, "pending")]
        [InlineData("slot-reuse", "synced")]
        [InlineData("slot-reuse", "forgotten")]
        [InlineData("slot-reuse", "pending")]
        public void Fresh_engine_reuses_slots_whose_clears_may_not_be_on_the_device(string password, string clears)
        {
            using var file = new TempFile();
            using var logFile = new TempFile();
            using var data = new ForgetfulFile(file.Filename);
            using var log = new ForgetfulFile(logFile.Filename);
            Setup(file.Filename, data, log, password);
            var armed = false;
            var settings = Settings(file.Filename, data, log, password);
            settings.CheckpointStage = stage =>
            {
                if (!armed || stage != "wal-slot-cleared") return;
                armed = false;
                if (clears == "forgotten") log.FailNextSync = true;
                else log.CrashAtNextSync = true;
            };
            using (var first = new LiteEngine(settings))
            using (var db = new LiteDatabase(first, disposeOnClose: false))
                RetireUnderAReader(first, db, () => armed = clears != "synced", fails: clears != "synced");
            if (password == null)
            {
                RetirementRoot(data.Durable).Should().BeGreaterThan(0, "the witness root is durable");
                BlankFrames(log.Live).Should().BeGreaterThan(0, "the checkpoint cleared the slots it retired");
                if (clears == "synced") BlankFrames(log.Durable).Should().Be(BlankFrames(log.Live), "the clears synced");
                else BlankFrames(log.Durable).Should().BeLessThan(BlankFrames(log.Live), "the device keeps the old frames");
            }
            (log.Pending > 0).Should().Be(clears == "pending", "only a crash before the sync leaves the clears dirty");
            Recover((data.Image(ImageKind.Lost), log.Image(ImageKind.Lost)), password, 9);
            Recover((data.Image(ImageKind.WrittenBack), log.Image(ImageKind.WrittenBack)), password, 9);

            // A new process: nothing it synced is remembered.
            DurableLogs.Forget(Path.GetFullPath(logFile.Filename));
            DurableHeaders.Forget(file.Filename);
            var overwrites = 0;
            log.BeforeWrite = (position, count) => { if (position < log.Length) overwrites++; };
            var durable = clears != "pending";
            try
            {
                var fresh = Settings(file.Filename, data, log, password);
                fresh.DurableCommits = durable;
                using var second = new LiteEngine(fresh);
                using var db = new LiteDatabase(second, disposeOnClose: false);
                SyncPowerLossModel.AssertRows(db, Rows, 9);
                CommitWithPowerLossImages(db, data, log, password, 9, 10, durable);
                overwrites.Should().BeGreaterThan(0, "the commit reused retired slots");
            }
            finally { log.BeforeWrite = null; }
            using (var third = new LiteEngine(Settings(file.Filename, data, log, password)))
            using (var db = new LiteDatabase(third, disposeOnClose: false))
            {
                SyncPowerLossModel.AssertRows(db, Rows, 10);
                Update(db, 11);
                DurableLogFlush(db).Should().BeTrue();
            }
            Recover((data.Durable, log.Durable), password, 11);
        }

        /// <summary>
        /// Commit <paramref name="value"/> to every row, taking at each WAL write of the commit and
        /// around its sync the images a power loss may leave: its pending writes lost, written back,
        /// written back with the last write torn, or only the commit's own writes written back (writes
        /// an earlier engine left pending lost). Each recovers <paramref name="previous"/> until the
        /// confirmation is whole on the device, and <paramref name="value"/> from then on, and, when the
        /// commit is <paramref name="durable"/>, once its sync succeeded.
        /// </summary>
        private static void CommitWithPowerLossImages(LiteDatabase db, ForgetfulFile data, ForgetfulFile log, string password,
            int previous, int value, bool durable = true, Action committed = null)
        {
            var images = new Dictionary<string, ((byte[] Data, byte[] Log) Files, HashSet<int> Allowed, List<string> Taken)>();
            var points = new List<string>();
            void Add(string taken, (byte[], byte[]) files, int allowed)
            {
                var key = Key(files);
                if (!images.TryGetValue(key, out var image)) images.Add(key, image = (files, new HashSet<int> { allowed }, new List<string>()));
                image.Allowed.IntersectWith(new[] { allowed });
                image.Taken.Add(taken + " expects " + allowed);
            }
            var older = (Data: data.Operations, Log: log.Operations);
            bool confirmed = false, synced = false;
            EngineState.SimulateProcessCrash = point =>
            {
                if (!point.StartsWith("wal-", StringComparison.Ordinal)) return;
                points.Add(point);
                confirmed |= point == "wal-confirmation-after-write";
                synced |= durable && point == "wal-after-durable-flush";
                // Before its sync the commit is on the device only where its confirmation was written
                // back whole; torn, the confirmation is invalid and so is the commit.
                foreach (var kind in Kinds)
                    Add($"{points.Count}:{point}:{kind}", (data.Image(kind), log.Image(kind)),
                        synced || (kind == ImageKind.WrittenBack && confirmed) ? value : previous);
                Add($"{points.Count}:{point}:own writes", (data.Image(ImageKind.WrittenBack, older.Data),
                    log.Image(ImageKind.WrittenBack, older.Log)), synced || confirmed ? value : previous);
            };
            try { Update(db, value); }
            finally { EngineState.SimulateProcessCrash = null; }
            committed?.Invoke();
            // Before the report: reading $database retries a log sync on an engine whose latest one did
            // not succeed or that tried none (WalKeptReport), which would sync an opted-out commit.
            var afterCommit = (data.Durable, log.Durable);
            DurableLogFlush(db).Should().Be(durable, durable ? "the commit is acknowledged durable" : "the engine opted out");
            points.Should().Contain(new[] { "wal-page-after-write", "wal-confirmation-after-write", "wal-after-durable-flush" });
            Add("after the commit", afterCommit, durable ? value : previous);
            images.Count.Should().BeGreaterThan(points.Count / 2, "most WAL writes leave images of their own");
            foreach (var image in images.Values)
            {
                image.Allowed.Should().HaveCount(1, "an image is either before or after the commit: {0}", string.Join(", ", image.Taken));
                try { Recover(image.Files, password, image.Allowed.Single()); }
                catch (Exception ex) { throw new InvalidOperationException("Power-loss image " + string.Join(", ", image.Taken), ex); }
            }
        }

        /// <summary>
        /// Commit values 1 to 9 under a live reader (so the checkpoint retires frames instead of
        /// emptying the WAL), then arm the fault and checkpoint: it throws when <paramref name="fails"/>.
        /// </summary>
        private static void RetireUnderAReader(ILiteEngine engine, LiteDatabase db, Action arm, bool fails)
        {
            for (var value = 1; value <= 5; value++) Update(db, value);
            using var reader = engine.Query("rows", new Query());
            reader.Read().Should().BeTrue("the first read registers the reader's snapshot");
            Worker(() =>
            {
                for (var value = 6; value <= 9; value++) Update(db, value);
                DurableLogFlush(db).Should().BeTrue("commit 9 is acknowledged durable");
                arm();
                Action checkpoint = () => db.Checkpoint();
                if (fails) checkpoint.Should().Throw<IOException>();
                else checkpoint();
            });
        }

        /// <summary>Rows 1..64 of value 0, an index on value, no automatic or closing checkpoint, an empty WAL.</summary>
        private static void Setup(string filename, ForgetfulFile data, ForgetfulFile log, string password)
        {
            using var engine = new LiteEngine(Settings(filename, data, log, password));
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            db.CheckpointSize = 0;
            db.Checkpoint();
        }

        private static EngineSettings Settings(string filename, ForgetfulFile data, ForgetfulFile log, string password) =>
            new EngineSettings { Filename = filename, DataStream = data, LogStream = log, Password = password };

        private static ILiteEngine Open(EngineSettings settings, bool shared) =>
            shared ? (ILiteEngine)new SharedEngine(settings) : new LiteEngine(settings);

        /// <summary>Open a device image as a copy: every row holds <paramref name="value"/> (documents and index).</summary>
        private static void Recover((byte[] Data, byte[] Log) files, string password, int value) =>
            FilePowerLossModel.Open(files, db => SyncPowerLossModel.AssertRows(db, Rows, value), password);

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static string Key((byte[] Data, byte[] Log) files)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(files.Data)) + Convert.ToBase64String(sha.ComputeHash(files.Log));
        }

        private static long RetirementRoot(byte[] data) => BitConverter.ToInt64(data, WalRetirement.RootPosition);

        private static int BlankFrames(byte[] log) => Enumerable.Range(0, log.Length / WalChecksum.FrameSize)
            .Count(frame => log.Skip(frame * WalChecksum.FrameSize).Take(WalChecksum.FrameSize).All(value => value == 0));

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static void Worker(Action action)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            });
            thread.Start();
            thread.Join();
            failure?.Throw();
        }
    }
}
#endif
