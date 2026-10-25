#if LITEDB_PREDEV
using System;
using System.Threading;

namespace LiteDB
{
    /// <summary>
    /// Explicit acknowledgements required by development prerelease builds of LiteDB.
    /// </summary>
    public static class LiteDBPragmas
    {
#if TESTING
        private static int _preDevRiskAcknowledged = 1;
#else
        private static int _preDevRiskAcknowledged;
#endif

        private const string PREDEV_WARNING =
            "This is a prerelease development build of LiteDB. Broken databases and data loss are expected, " +
            "and you are on your own if it breaks; support may not be available. DO NOT RUN IN PRODUCTION! " +
            "To acknowledge this risk, call " +
            "LiteDBPragmas.I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS() before constructing LiteDatabase.";

        /// <summary>
        /// Acknowledges for this process that this prerelease build can break databases and must not be used in production.
        /// </summary>
        public static void I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS()
        {
            Interlocked.Exchange(ref _preDevRiskAcknowledged, 1);
        }

        internal static void EnsurePreDevRiskAcknowledged()
        {
            if (Volatile.Read(ref _preDevRiskAcknowledged) == 0)
            {
                throw new InvalidOperationException(PREDEV_WARNING);
            }
        }

#if TESTING
        internal static void ResetPreDevRiskAcknowledgementForTesting()
        {
            Volatile.Write(ref _preDevRiskAcknowledged, 0);
        }
#endif
    }
}
#endif
