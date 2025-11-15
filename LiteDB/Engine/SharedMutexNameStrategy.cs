namespace LiteDB.Engine;

/// <summary>
/// Specifies the strategy used to generate mutex names for shared database access across multiple processes.
/// </summary>
/// <remarks>
/// <para>
/// In shared mode (<see cref="ConnectionType.Shared"/>), LiteDB uses named mutexes to coordinate access
/// across multiple processes. The mutex name is derived from the database filename.
/// </para>
/// <para>
/// Different strategies handle special characters and path variations differently, which may be necessary
/// for compatibility across different operating systems or file system configurations.
/// </para>
/// </remarks>
public enum SharedMutexNameStrategy
{
    /// <summary>
    /// Uses the default mutex naming strategy. The filename is used directly with basic normalization.
    /// </summary>
    /// <remarks>
    /// This is the default strategy and works for most scenarios. The filename path is normalized
    /// to ensure consistent mutex names regardless of path format (e.g., relative vs. absolute paths).
    /// </remarks>
    Default,

    /// <summary>
    /// Uses URI escape encoding to generate the mutex name from the filename.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This strategy encodes special characters in the filename using URI-safe encoding (percent-encoding).
    /// This ensures the mutex name contains only characters valid for named mutexes across all platforms.
    /// </para>
    /// <para>
    /// Use this strategy when the database filename contains special characters that might cause issues
    /// with mutex naming on certain platforms.
    /// </para>
    /// </remarks>
    UriEscape,

    /// <summary>
    /// Uses SHA-1 hashing to generate the mutex name from the filename.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This strategy creates a SHA-1 hash of the filename to generate a fixed-length, platform-safe mutex name.
    /// This ensures the mutex name is always valid regardless of the filename's length or characters.
    /// </para>
    /// <para>
    /// Use this strategy when dealing with very long filenames or when you need guaranteed compatibility
    /// across all platforms. The SHA-1 hash provides a deterministic, fixed-length mutex name.
    /// </para>
    /// <para>
    /// Note: SHA-1 is cryptographically broken and not collision-resistant; while collisions are unlikely for filesystem paths, this strategy should not be relied upon for security.
    /// </para>
    /// </remarks>
    Sha1Hash
}