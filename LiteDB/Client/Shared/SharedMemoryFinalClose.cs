#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    internal static class SharedMemoryFinalClose
    {
        // The last reference includes facades, generations, requests and holders.
        // No local caller can still use the cache or enter a writable core here.
        internal static void Run(SharedMemoryReaderTable table)
        {
            var authority = table.Authority;
            using var bootstrap = SharedMutexFactory.Create(SharedMutexNameFactory.CreateUsingSha1(
                SharedMemoryAuthority.PathFor(table.Settings.Filename)) + ".Bootstrap");
            var held = false;
            var mainHeld = false;
            using var main = SharedMutexFactory.Create(SharedMutexNameFactory.Create(
                table.Settings.Filename, table.Settings.SharedMutexNameStrategy));
            try
            {
                try { held = bootstrap.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                if (!held || !authority.Locks.TryExclusiveDms()) return;
                Stage("exclusive");
                // An interrupted/stopped core's uncertain fence is repaired only by
                // the next ordinary protected opener, never blessed by its disposal.
                if (SharedMemoryReaderTable.Scan(authority) == null) return;
                var abandoned = false;
                try { mainHeld = main.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { mainHeld = true; abandoned = true; }
                if (!mainHeld || abandoned) return;
                Stage("main");
                if (!table.Settings.ReadOnly && !table.Settings.ReadOnlyStorage)
                {
                    var settings = table.Settings.Clone();
                    settings.Upgrade = settings.AutoRebuild = false;
                    settings.SharedMaintenance = null;
                    settings.SharedAdmission = table.Admission;
                    settings.SharedReaderVersions = () => Array.Empty<int>();
                    settings.CoordinationSignals = table;
                    settings.DurableCommitPublication = table.PublishDurableCommit;
                    table.EnterWriter();
                    var publish = false;
                    LiteEngine core = null;
                    try
                    {
                        settings.SharedWriterTenure = table.WriterTenure;
                        table.BeginOpenRecovery();
                        Stage("write");
                        core = new LiteEngine(settings);
                        core.SyncSharedRecovery();
                        table.Opened(core.ReadVersion);
                        Stage("opened");
                        var errors = core.Close(final: true);
                        publish = !core.StorageUncertain && errors.Count == 0;
                        Stage("checkpointed");
                    }
                    finally
                    {
                        try { core?.Close(checkpoint: false); }
                        finally { table.ExitWriter(publish); }
                    }
                    if (!publish) return;
                }
                // Keep valid envelopes/dimensions for P2-G reuse. Not-Ready prevents
                // admission until an independent next opener validates DB and WAL.
                authority.Map.Store(SharedMemoryLayout.FastAdmissionDisabled, 1);
                Stage("disabled");
                authority.Map.Store(SharedMemoryLayout.Control, 0);
                Stage("retired");
                SharedWakeSignal.Retire(authority.Header.Authority);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is LiteException)
            {
                // Preserve DB/WAL and uncertain fences. Dispose remains best effort.
            }
            finally
            {
                try { if (mainHeld) { main.ReleaseMutex(); Stage("main-released"); } }
                finally { if (held) bootstrap.ReleaseMutex(); }
                // Authority.Dispose returns the slot and DMS after WRITE and main.
            }
        }
#if DEBUG || TESTING
        internal static Action<string> FinalCloseStage;
#endif
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        internal static void Stage(string stage)
        {
#if DEBUG || TESTING
            Reachability.FaultPoint("FinalCloseStage");
            FinalCloseStage?.Invoke(stage);
#endif
        }
    }
}
#endif
