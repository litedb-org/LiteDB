using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Specifies the connection type for opening a LiteDB database.
    /// </summary>
    /// <remarks>
    /// The connection type determines how the database engine is instantiated and how it manages concurrent access.
    /// </remarks>
    public enum ConnectionType
    {
        /// <summary>
        /// Direct connection mode. Each <see cref="LiteDatabase"/> instance creates its own dedicated <see cref="LiteEngine"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the default and recommended mode for most scenarios. The engine is disposed when the <see cref="LiteDatabase"/> is disposed.
        /// </para>
        /// <para>
        /// Use this mode when you want exclusive access to the database file or when the database is accessed from a single application instance.
        /// </para>
        /// </remarks>
        Direct,

        /// <summary>
        /// Shared connection mode. Multiple <see cref="LiteDatabase"/> instances can share the same underlying <see cref="SharedEngine"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This mode uses a mutex-based locking mechanism to coordinate access across multiple processes.
        /// The engine remains open as long as at least one database instance is active.
        /// </para>
        /// <para>
        /// Use this mode when you need to access the database from multiple processes concurrently.
        /// Note that shared mode is not supported on all platforms (requires named mutex support).
        /// </para>
        /// </remarks>
        Shared

        // Future connection types under consideration:
        // NamedPipes
        // Tcp
        // Rest
    }
}