namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        partial void CrashPoint(string phase);

#if DEBUG || TESTING
        partial void CrashPoint(string phase)
        {
            _state.CrashPoint(phase);
        }

        internal void TestCrashPoint(string phase) => _state.CrashPoint(phase);
#endif
    }
}
