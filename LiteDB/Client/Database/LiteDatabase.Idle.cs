using System;

namespace LiteDB
{
    public partial class LiteDatabase
    {
        /// <summary>Synchronously release an idle one-file Shared participant for this filename.
        /// Dispose all databases and results first. Active participants are left open.
        /// Use before immediate deletion or replacement of a file retained after Dispose.</summary>
        /// <param name="filename">Database filename, resolved as for a new connection.</param>
        public static void ReleaseIdle(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) throw new ArgumentException("A filename is required.", nameof(filename));
#if NET8_0_OR_GREATER
            Client.Shared.SharedParticipantRegistry.ReleaseIdle(filename);
#endif
        }

        /// <summary>Synchronously release all idle one-file Shared participants in this process.
        /// Active databases, transactions and results remain open.</summary>
        public static void ReleaseAllIdle()
        {
#if NET8_0_OR_GREATER
            Client.Shared.SharedParticipantRegistry.ReleaseAllIdle();
#endif
        }
    }
}
