namespace LiteDB
{
    /// <summary>Observed Shared read admission state; individual queries may still use the protected path.</summary>
    public enum SharedReadPath
    {
        /// <summary>The connection has not yet attempted mapped attachment.</summary>
        Uninitialized,
        /// <summary>Queries use database-mutex protection.</summary>
        Protected,
        /// <summary>A mapped coordination authority is attached.</summary>
        Mapped,
        /// <summary>The attached authority has been revoked or invalidated.</summary>
        Revoked,
        /// <summary>The connection has been disposed.</summary>
        Disposed
    }
}
