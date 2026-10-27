using System;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private static void ValidateOneFileSettings(EngineSettings settings)
        {
            if (!settings.SharedOneFile) return;
#if NET8_0_OR_GREATER
            if (!NativeAdmission.IsFile(settings)) throw new NotSupportedException("SharedOneFile requires a physical database file.");
            if (!SharedMemoryAuthority.IsPlatformQualified)
                throw new PlatformNotSupportedException("ABI-2 Shared currently requires a qualified Linux x64 runtime.");
#else
            throw new PlatformNotSupportedException("SharedOneFile requires .NET 8 or later.");
#endif
        }

#if (DEBUG || TESTING) && NET8_0_OR_GREATER
        internal SharedMemoryReaderTable MappedReaderTable => (_readers as SharedMemoryReaders)?.Table;
#endif
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
        private Action<string> HandoffPublication;
#endif
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        private void CaptureHandoffPublication()
        {
#if (DEBUG || TESTING) && NET8_0_OR_GREATER
            HandoffPublication = SharedMemoryReaderTable.PublicationStage;
#endif
        }

        // A retained writable core protects a fresh committed snapshot while
        // the usual read cache is installed, without ending its writer tenure.
        private IBsonDataReader TryQueryRetained(string collection, Query query)
        {
            var retained = _pin;
            if (retained == null || !this.TryEnterPin(retained)) return null;
            var transferred = false;
            try
            {
                if (_engine.HasActiveTransaction) return null;
                var snapshot = this.TryOpenSnapshot(false);
                if (snapshot == null) return null;
                transferred = true;
                return this.QuerySnapshot(collection, query, snapshot, retained);
            }
            finally { if (!transferred) this.CloseDatabase(retained); }
        }

        private void ReleaseSnapshotUse(SharedMutexPin use)
        {
            if (use != null) this.CloseDatabase(use);
            else _owner.Exit();
        }

        private bool UsesMappedTenure => _settings.SharedOneFile && !_settings.ReadOnly && !_settings.ReadOnlyStorage;

        private SharedMutexPin AcquirePin()
        {
#if NET8_0_OR_GREATER
            if (this.UsesMappedTenure)
            {
                var table = (_readers as SharedMemoryReaders)?.Table;
                if (table == null) throw new ObjectDisposedException(nameof(SharedEngine));
                using (table.RequestWriter(this.IsDisposed))
                {
                    if (System.Threading.Volatile.Read(ref _disposed) != 0)
                        throw new ObjectDisposedException(nameof(SharedEngine));
                    return SharedMutexPin.AcquireTenure(_mutex, _turnstile, this.HasMutexWaiters, this.ClosePin,
                        new SharedWriterTenure(table));
                }
            }
#endif
            using (this.RequestMappedTurn())
                return SharedMutexPin.Acquire(_mutex, _turnstile, this.HasMutexWaiters, this.ClosePin, this.PinIdleLimit, this.PinHoldLimit);
        }

        private bool IsDisposed() => System.Threading.Volatile.Read(ref _disposed) != 0;

        private IDisposable RequestMappedTurn()
        {
#if NET8_0_OR_GREATER
            if (!(_readers is SharedMemoryReaders mapped)) return null;
            var table = mapped.Table;
            if (table == null) throw new ObjectDisposedException(nameof(SharedEngine));
            return table.RequestWriter(this.IsDisposed);
#else
            return null;
#endif
        }

        private static void BindParticipantSettings(EngineSettings settings, ISharedReaderRegistry readers)
        {
#if NET8_0_OR_GREATER
            if (readers is SharedMemoryReaders mapped)
            {
                settings.TransactionOwner = mapped.Table.LocalTransactions.Token;
                settings.SharedMaintenance = () => mapped.Table.Maintain(settings);
                settings.CheckpointBackoff = mapped.Table.Settings.CheckpointBackoff;
                settings.SharedDurability = mapped.Table.Settings.SharedDurability;
                settings.CoordinationSignals = mapped.Table;
                settings.DurableCommitPublication = mapped.Table.PublishDurableCommit;
            }
#endif
        }

        private static ISharedReaderRegistry CreateReaderRegistry(EngineSettings settings)
        {
            if (!settings.SharedOneFile) return new SharedReaderRegistry(settings.Filename, settings.SharedReaderFiles);
#if NET8_0_OR_GREATER
            if (!NativeAdmission.IsFile(settings)) throw new NotSupportedException("SharedOneFile requires a physical database file.");
            return SharedParticipantRegistry.Attach(settings);
#else
            throw new PlatformNotSupportedException("SharedOneFile requires .NET 8 or later.");
#endif
        }
    }
}
