using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteDB.Tests.Safety
{
    /// <summary>Which injector models apply to a step.</summary>
    [Flags]
    internal enum TeardownModels
    {
        /// <summary>The action ran, then failed (thrown at the step's After marker).</summary>
        FailInside = 1,
        /// <summary>The action failed before any effect (thrown at the step's Before marker).</summary>
        Skip = 2,
        Both = FailInside | Skip
    }

    /// <summary>
    /// One teardown step: what can fail there in reality, which injector models apply, and what a
    /// skip of it may leave behind (the step's own obligations, as oracle violation kinds). A skip
    /// leaves its own obligations undone by definition; anything else left undone, and anything at
    /// all after a fail-inside fault, is a violation of the path's cleanup.
    /// </summary>
    internal sealed class TeardownStepInfo
    {
        public string Name { get; set; }
        public string Fails { get; set; }
        public TeardownModels Models { get; set; }
        public string[] SkipLeaves { get; set; }
        /// <summary>The realistic exception the step's action throws; default an IOException.</summary>
        public Func<string, Exception> Fault { get; set; }
    }

    /// <summary>
    /// Every teardown step marker in the library. A step without an entry fails the sweep ("new step X
    /// has no catalog entry"), so a new step states its failure model when it is added. The literal
    /// names here are also the evidence that .github/safety/fault-points.json links to each
    /// <c>teardown-step</c> hook. Violation kinds: <c>connection.*</c> (ConnectionClean),
    /// <c>quiescent.*</c> (Quiescent), <c>ownership.*</c>, <c>leak.page-buffers</c>.
    /// </summary>
    internal static partial class TeardownStepCatalog
    {
        private const string Handles = "quiescent.handles";
        private const string Scratch = "quiescent.scratch";
        private const string Mutex = "quiescent.mutex";
        private const string Readers = "quiescent.readers";
        private const string Threads = "quiescent.threads";
        private const string Transactions = "connection.transactions";
        private const string OwnerThread = "connection.threads";
        private const string Pages = LeakedPages;

        /// <summary>The violation kind of page buffers finalized while still in use.</summary>
        public const string LeakedPages = "leak.page-buffers";

        public static readonly IReadOnlyDictionary<string, TeardownStepInfo> Steps = Build(new List<TeardownStepInfo>
        {
            Step("LiteEngine.Close.monitor", "disposing open transactions (page, lock and reader release)", Transactions, Pages, Handles),
            Step("LiteEngine.Close.checkpoint", "the close checkpoint's WAL-to-data I/O"),
            Step("LiteEngine.Close.disk", "closing the data/WAL streams and deleting an empty WAL", Handles, Pages),
            Step("LiteEngine.Close.sort-disk", "closing and deleting the sort scratch file", Handles, Scratch),
            Step("LiteEngine.Close.locker", "disposing the lock service"),
            Step("LiteEngine.CloseOnError.monitor", "disposing open transactions (page, lock and reader release)", Transactions, Pages, Handles),
            Step("LiteEngine.CloseOnError.mark-invalid", "writing the invalid-datafile marker"),
            Step("LiteEngine.CloseOnError.disk", "closing the data/WAL streams", Handles, Pages),
            Step("LiteEngine.CloseOnError.sort-disk", "closing and deleting the sort scratch file", Handles, Scratch),
            Step("LiteEngine.CloseOnError.locker", "disposing the lock service"),
            Step("TransactionMonitor.Dispose.transaction", "disposing one open transaction (its pages and its disk reader)", Transactions, Pages, Handles),
            Step("TransactionMonitor.Dispose.slot", "disposing the thread transaction slot"),
            Step("TransactionMonitor.Dispose.explicit-aborted", "disposing the explicit-abort marks"),
            Step("TransactionService.Dispose.snapshot-pages", "releasing a snapshot's page leases", Pages),
            Step("TransactionService.Dispose.snapshot-lock", "releasing a write snapshot's collection lock"),
            Step("TransactionService.Dispose.reader", "returning the transaction's disk reader stream", Pages, Handles),
            Step("DiskService.Dispose.data-pool", "closing the data file streams", Handles),
            Step("DiskService.Dispose.log-pool", "closing the WAL streams", Handles),
            Step("DiskService.Dispose.delete-log", "deleting an empty WAL file"),
            Step("DiskService.Dispose.cache", "disposing the page cache (fails while pages are in use)", Pages),
            Step("SortService.Dispose.container", "disposing a sort container"),
            Step("SortService.Dispose.return-position", "returning a container's scratch position"),
            Step("SortService.Dispose.return-reader", "returning the scratch reader stream", Handles),
            Step("SortDisk.Dispose.pool", "closing the scratch file streams", Handles),
            Step("SortDisk.Dispose.delete", "deleting the scratch file", Scratch),
            Step("AesStream.Dispose.writer", "closing the encrypting writer (flushes the final block)", Handles),
            Step("AesStream.Dispose.reader", "closing the decrypting reader", Handles),
            Step("AesStream.Dispose.encryptor", "disposing the encryptor transform"),
            Step("AesStream.Dispose.decryptor", "disposing the decryptor transform"),
            Step("AesStream.Dispose.aes", "disposing the cipher"),
            Step("AesStream.Dispose.stream", "closing the underlying file stream", Handles),
            Step("LiteDatabase.Dispose.checkpoint-override", "restoring the CHECKPOINT pragma (a write: lock timeout or I/O)",
                fault: step => LiteException.LockTimeout("teardown-sweep fault at " + step, TimeSpan.Zero)),
            Step("BsonDataReader.Dispose.source", "disposing the query pipeline (cursor transaction, sort)", Transactions, Pages, Handles, Scratch),
            Step("RebuildService.DiscardReplacement.delete-data", "deleting the unpublished replacement data file"),
            Step("RebuildService.DiscardReplacement.delete-log", "deleting the unpublished replacement WAL"),
            Step("SharedEngine.Dispose.retire-reads", "retiring the cached coordinated snapshot (closes its engine and lease)", Handles, Readers),
            Step("SharedEngine.Dispose.wait-pin", "waiting for the pin holder, which rethrows the holder's close failure"),
            Step("SharedEngine.Dispose.early-handles", "closing cached file handles while the pin's holder still closes", Handles),
            Step("SharedEngine.Dispose.early-readers", "closing the reader registry while the pin's holder still closes", Handles, Readers),
            Step("SharedEngine.Dispose.handles", "closing cached file handles", Handles),
            Step("SharedEngine.Dispose.readers", "closing the reader registry's slot file", Handles, Readers),
            Step("SharedEngine.Dispose.coordination", "disposing mapped coordination and retiring its fallback files", Handles),
            Step("SharedEngine.CheckpointOnDispose.close-finally", "opening an engine for the final checkpoint and closing it"),
            Step("SharedEngine.CheckpointAfterLastReader.close-finally", "opening an engine for the last reader's checkpoint and closing it"),
            Step("SharedEngine.ClosePin.coordination", "disposing coordination when the pin ends after Dispose", Handles),
            Step("SharedEngine.OnOwnerExited.idle-handles", "closing idle cached handles after an exited owner", Handles),
            Step("SharedMutexPin.Hold.close", "the pin's close callback (its own steps carry the skip model)", models: TeardownModels.FailInside),
            Step("SharedMutexOwner.Exit.scope-release", "releasing a scoped ownership's OS mutex", Mutex, Threads),
            // The holder thread that never got the release keeps owning the OS mutex, so it cannot exit either.
            Step("SharedMutexOwner.ReleaseAll.send-release", "the holder releasing the OS mutex on Dispose", Mutex, Threads, OwnerThread),
            Step("SharedMutexOwner.ReleaseExitedOwner.cleanup", "the exited owner's cleanup callback (its own steps carry the skip model)",
                models: TeardownModels.FailInside),
            Step("SharedDataReader.Dispose.lease", "closing a leased reader's lease file", Handles, Readers),
        });

        private static IReadOnlyDictionary<string, TeardownStepInfo> Build(List<TeardownStepInfo> steps)
        {
            OverlaySteps(steps);
            return steps.ToDictionary(step => step.Name, StringComparer.Ordinal);
        }

        /// <summary>Steps of teardown paths that exist only on another revision, added by an overlay file.</summary>
        static partial void OverlaySteps(List<TeardownStepInfo> steps);

        public static TeardownStepInfo Find(string step) => Steps.TryGetValue(step, out var info) ? info : null;

        /// <summary>Steps whose action exists only on some platforms: (does it run here, why not elsewhere).</summary>
        private static readonly Dictionary<string, (Func<bool> Here, string Reason)> PlatformOnly =
            new Dictionary<string, (Func<bool>, string)>(StringComparer.Ordinal)
            {
                ["SharedEngine.Dispose.handles"] = (IsWindows, CachedHandles),
                ["SharedEngine.Dispose.early-handles"] = (IsWindows, CachedHandles),
                ["SharedEngine.OnOwnerExited.idle-handles"] = (IsWindows, CachedHandles),
            };

        private const string CachedHandles = "Shared mode caches file handles only on Windows (SharedFileHandles.IsSupportedFor)";

        private static bool IsWindows() => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        /// <summary>Why <paramref name="step"/> cannot run on this platform, or null when it can.</summary>
        public static string UnreachableHere(string step) =>
            PlatformOnly.TryGetValue(step, out var entry) && !entry.Here() ? entry.Reason : null;

        /// <summary>The fault a case throws at <paramref name="step"/>.</summary>
        public static Exception Fault(string step, FaultModel model, int occurrence)
        {
            var label = $"{step}#{occurrence} ({(model == FaultModel.Skip ? "skip" : "fail-inside")})";
            var factory = Find(step)?.Fault;
            return factory != null ? factory(label) : new IOException("teardown-sweep fault at " + label);
        }

        private static TeardownStepInfo Step(string name, string fails, params string[] skipLeaves) =>
            new TeardownStepInfo { Name = name, Fails = fails, Models = TeardownModels.Both, SkipLeaves = skipLeaves };

        private static TeardownStepInfo Step(string name, string fails, TeardownModels models, params string[] skipLeaves) =>
            new TeardownStepInfo { Name = name, Fails = fails, Models = models, SkipLeaves = skipLeaves };

        private static TeardownStepInfo Step(string name, string fails, Func<string, Exception> fault, params string[] skipLeaves) =>
            new TeardownStepInfo { Name = name, Fails = fails, Models = TeardownModels.Both, SkipLeaves = skipLeaves, Fault = fault };
    }
}
