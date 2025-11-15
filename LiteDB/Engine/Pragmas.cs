namespace LiteDB.Engine
{
    /// <summary>
    /// Defines constant names for internal engine variables (pragmas) that can be queried or modified using <see cref="ILiteDatabase.Pragma(string)"/> or <see cref="ILiteDatabase.Pragma(string, BsonValue)"/>.
    /// </summary>
    public static class Pragmas
    {
        /// <summary>
        /// User-defined version number for tracking database schema versions.
        /// </summary>
        public const string USER_VERSION = nameof(USER_VERSION);

        /// <summary>
        /// Database collation setting for string comparison and sorting.
        /// </summary>
        public const string COLLATION = nameof(COLLATION);

        /// <summary>
        /// Timeout in seconds for acquiring locks during transactions.
        /// </summary>
        public const string TIMEOUT = nameof(TIMEOUT);

        /// <summary>
        /// Maximum database size limit in bytes.
        /// </summary>
        public const string LIMIT_SIZE = nameof(LIMIT_SIZE);

        /// <summary>
        /// Flag indicating whether dates are deserialized in UTC timezone.
        /// </summary>
        public const string UTC_DATE = nameof(UTC_DATE);

        /// <summary>
        /// Auto-checkpoint threshold in pages.
        /// <para>Page size is defined by <c>Engine.Constants.PAGE_SIZE</c> (default is 8 KB.)</para>
        /// </summary>
        public const string CHECKPOINT = nameof(CHECKPOINT);
    }
}