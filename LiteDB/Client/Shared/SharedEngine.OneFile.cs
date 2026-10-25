using System;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private static ISharedReaderRegistry CreateReaderRegistry(EngineSettings settings)
        {
            if (!settings.SharedOneFile) return new SharedReaderRegistry(settings.Filename, settings.SharedReaderFiles);
#if NET8_0_OR_GREATER
            if (!NativeAdmission.IsFile(settings)) throw new NotSupportedException("SharedOneFile requires a physical database file.");
            return new SharedMemoryReaders(SharedMemoryReaderTable.Open(settings));
#else
            throw new PlatformNotSupportedException("SharedOneFile requires .NET 8 or later.");
#endif
        }
    }
}
