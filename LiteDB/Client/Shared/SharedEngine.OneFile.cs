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
                throw new PlatformNotSupportedException("ABI-2 Shared prototype currently requires Linux x64 qualification.");
#else
            throw new PlatformNotSupportedException("SharedOneFile requires .NET 8 or later.");
#endif
        }

#if (DEBUG || TESTING) && NET8_0_OR_GREATER
        internal SharedMemoryReaderTable MappedReaderTable => (_readers as SharedMemoryReaders)?.Table;
#endif
        private static void BindParticipantSettings(EngineSettings settings, ISharedReaderRegistry readers)
        {
#if NET8_0_OR_GREATER
            if (readers is SharedMemoryReaders mapped)
            {
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
