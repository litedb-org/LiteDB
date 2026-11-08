namespace LiteDB.Client.Shared
{
    internal static class SharedProtocolDefaults
    {
        internal static bool OneFile
        {
            get
            {
#if NET8_0_OR_GREATER
                return SharedMemoryAuthority.IsPlatformQualified;
#else
                return false;
#endif
            }
        }
    }
}
