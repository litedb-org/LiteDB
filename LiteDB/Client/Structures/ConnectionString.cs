using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LiteDB.Engine;

namespace LiteDB
{
    /// <summary>
    /// Manage ConnectionString to connect and create databases. Connection string are NameValue using Name1=Value1; Name2=Value2
    /// </summary>
    public class ConnectionString
    {
        private readonly Dictionary<string, string> _values;
        private int? _transactionPageLimit;

        /// <summary>Opt in to compact document writes and lazy v10 promotion. Existing documents stay readable in place.</summary>
        public bool CompactStorage { get; set; } = false;

        /// <summary>
        /// "memory profile": Balanced (default), LowMemory, or Throughput.
        /// Explicit cache and transaction limits override these defaults.
        /// </summary>
        public MemoryProfile MemoryProfile { get; set; } = MemoryProfile.Balanced;

        /// <summary>
        /// "connection": Return how engine will be open (default: Direct)
        /// </summary>
        public ConnectionType Connection { get; set; } = ConnectionType.Direct;

        /// <summary>
        /// "filename": Full path or relative path from DLL directory
        /// </summary>
        public string Filename { get; set; } = "";

        /// <summary>
        /// "password": Database password used to encrypt/decypted data pages
        /// </summary>
        public string Password { get; set; } = null;

        /// <summary>
        /// "initial size": If database is new, initialize with allocated space - support KB, MB, GB (default: 0)
        /// </summary>
        public long InitialSize { get; set; } = 0;

        /// <summary>
        /// "cache size": Soft page-cache target in bytes. Supports KB, MB,
        /// and GB suffixes. Zero selects the profile's storage-specific default.
        /// </summary>
        public long CacheSize { get; set; } = 0;

        /// <summary>
        /// "transaction pages": Per-transaction cooperative safepoint limit.
        /// Defaults to the selected profile; explicit values must be positive.
        /// </summary>
        public int TransactionPageLimit
        {
            get => _transactionPageLimit ?? MemoryProfileDefaults.GetTransactionPageLimit(this.MemoryProfile);
            set => _transactionPageLimit = value;
        }

        /// <summary>
        /// "readonly": Open datafile in readonly mode (default: false)
        /// </summary>
        public bool ReadOnly { get; set; } = false;

        /// <summary>
        /// "upgrade": Check if data file is an old version and convert before open (default: false)
        /// </summary>
        public bool Upgrade { get; set; } = false;

        /// <summary>
        /// "auto-rebuild": If last close database exception result a invalid data state, rebuild datafile on next open (default: false)
        /// </summary>
        public bool AutoRebuild { get; set; } = false;

        /// <summary>
        /// "reject invalid local time": Throw ArgumentException instead of storing a document containing a Local or
        /// Unspecified DateTime that does not exist in TimeZoneInfo.Local (the hour skipped by daylight saving), which
        /// is otherwise stored as the following valid hour. Depends on the machine time zone: never fires on a UTC
        /// host, and can reject date-only values in zones that switch at midnight. Prefer storing UTC (default: false)
        /// </summary>
        public bool RejectInvalidLocalTime { get; set; } = false;

        /// <summary>
        /// "durable commits": Sync each committed transaction to the storage device before Commit returns, so it
        /// survives power loss and operating system crashes, at about one device sync per commit. Set to false for
        /// the behaviour before 6.0: commits are handed to the operating system only, which is much faster for many
        /// small transactions and still survives a process crash, but a power loss or operating system crash can lose
        /// the most recent commits or, rarely, leave them partially applied. Not stored in the data file (default: true)
        /// </summary>
        public bool DurableCommits { get; set; } = true;

        /// <summary>
        /// "collation": Set default collaction when database creation (default: "[CurrentCulture]/IgnoreCase")
        /// </summary>
        public Collation Collation { get; set; }

        /// <summary>
        /// Initialize empty connection string
        /// </summary>
        public ConnectionString()
        {
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parse key/value options or a filename. Input containing '=' is parsed as options
        /// when it also contains ';' or starts with a built-in option name (case-insensitive).
        /// Unknown single-key input is treated as a filename. Set Filename directly to
        /// avoid parsing ambiguous paths, or use an explicit quoted filename option.
        /// </summary>
        public ConnectionString(string connectionString)
            : this()
        {
            if (string.IsNullOrEmpty(connectionString)) throw new ArgumentNullException(nameof(connectionString));

            var quotedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // create a dictionary from string name=value collection
            if (LooksLikeKeyValueConnectionString(connectionString))
            {
                _values.ParseKeyValue(connectionString, quotedValues);
            }
            else
            {
                _values["filename"] = connectionString.Trim();
            }

            // setting values to properties
            this.Connection = _values.GetValue("connection", this.Connection);
            this.Filename = _values.GetValue("filename", this.Filename);

            this.Password = _values.GetValue("password", this.Password);

            // Keep the historical password= behavior while allowing an explicit
            // password="" to preserve a programmatically assigned empty password.
            if (this.Password == string.Empty && !quotedValues.Contains("password"))
            {
                this.Password = null;
            }

            this.InitialSize = _values.ContainsKey("initialsize") ?
                _values.GetFileSize("initialsize", this.InitialSize) :
                _values.GetFileSize("initial size", this.InitialSize);
            if (_values.TryGetValue("memory profile", out var profile)) this.MemoryProfile = MemoryProfileDefaults.Parse(profile);
            this.CacheSize = _values.TryGetValue("cache size", out var cacheSizeText) ?
                ParseCacheSize(cacheSizeText) : this.CacheSize;
            if (_values.ContainsKey("transaction pages")) this.TransactionPageLimit = _values.GetValue<int>("transaction pages");

            if (this.CacheSize < 0 || this.TransactionPageLimit <= 0)
            {
                throw new LiteException(0, "`cache size` must be non-negative and `transaction pages` must be greater than zero");
            }
            this.CompactStorage = _values.GetValue("compact storage", this.CompactStorage);
            this.ReadOnly = _values.GetValue("readonly", this.ReadOnly);

            this.Collation = _values.ContainsKey("collation") ? new Collation(_values.GetValue<string>("collation")) : this.Collation;

            this.Upgrade = _values.GetValue("upgrade", this.Upgrade);
            this.AutoRebuild = _values.GetValue("auto-rebuild", this.AutoRebuild);
            this.RejectInvalidLocalTime = _values.GetValue("reject invalid local time", this.RejectInvalidLocalTime);
            this.DurableCommits = _values.GetValue("durable commits", this.DurableCommits);
        }

        private static bool LooksLikeKeyValueConnectionString(string connectionString)
        {
            var equals = connectionString.IndexOf('=');
            if (equals == -1) return false;

            // Options following a path must be parsed (and rejected if malformed),
            // never silently included in a filename, even when it has directory separators.
            if (connectionString.IndexOf(';') >= 0) return true;

            var firstKey = connectionString.Substring(0, equals).Trim();

            return firstKey.Equals("filename", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("initialsize", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("initial size", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("compact storage", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("readonly", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("upgrade", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("auto-rebuild", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("reject invalid local time", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("durable commits", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("collation", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("memory profile", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("cache size", StringComparison.OrdinalIgnoreCase) ||
                firstKey.Equals("transaction pages", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Get value from parsed connection string. Returns null if not found
        /// </summary>
        public string this[string key] => _values.GetOrDefault(key);

        private static long ParseCacheSize(string text)
        {
            var match = Regex.Match(text.Trim(), @"^([0-9]+)\s*([tgmk])?(b|byte|bytes)?$", RegexOptions.IgnoreCase);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
            {
                throw new LiteException(0, "Invalid connection string value for `cache size`");
            }

            var unit = match.Groups[2].Value.ToLowerInvariant();
            var exponent = unit.Length == 0 ? 0 : "kmgt".IndexOf(unit, StringComparison.Ordinal) + 1;
            try
            {
                for (var i = 0; i < exponent; i++) value = checked(value * 1024);
            }
            catch (OverflowException)
            {
                throw new LiteException(0, "`cache size` exceeds the supported byte range");
            }

            if (value > 0 && value < 1024L * 1024 && unit.Length == 0 && match.Groups[3].Length == 0)
            {
                throw new LiteException(0, "`cache size` values below 1 MB must include a size unit (for example, `512KB`)");
            }

            return value;
        }

        /// <summary>
        /// Create ILiteEngine instance according string connection parameters. For now, only Local/Shared are supported
        /// </summary>
        internal ILiteEngine CreateEngine(Action<EngineSettings> engineSettingsAction = null)
        {
            var settings = new EngineSettings
            {
                Filename = this.Filename,
                Password = this.Password,
                InitialSize = this.InitialSize,
                MemoryProfile = this.MemoryProfile,
                CacheSize = this.CacheSize,
                TransactionPageLimit = this.TransactionPageLimit,
                ReadOnly = this.ReadOnly,
                CompactStorage = this.CompactStorage,
                Collation = this.Collation,
                Upgrade = this.Upgrade,
                AutoRebuild = this.AutoRebuild,
                RejectInvalidLocalTime = this.RejectInvalidLocalTime,
                DurableCommits = this.DurableCommits,
            };

            engineSettingsAction?.Invoke(settings);

            // create engine implementation as Connection Type
            if (this.Connection == ConnectionType.Direct)
            {
                return new LiteEngine(settings);
            }
            else if (this.Connection == ConnectionType.Shared)
            {
                return new SharedEngine(settings);
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Returns a diagnostic representation of this connection string. If a
        /// password is present, its value is redacted.
        /// </summary>
        public override string ToString() => BuildConnectionString(false);

        /// <summary>
        /// Returns a connection string that includes the plaintext password.
        /// Do not log or otherwise expose the returned value.
        /// </summary>
        public string ToStringWithPassword() => BuildConnectionString(true);

        /// <summary>
        /// Returns a connection-string representation and optionally includes
        /// the plaintext password.
        /// </summary>
        /// <param name="includePlaintextPassword">Whether to include the plaintext password.</param>
        [Obsolete("Use ToStringWithPassword() to include the plaintext password.")]
        public string ToString(bool includePlaintextPassword) =>
            BuildConnectionString(includePlaintextPassword);

        private string BuildConnectionString(bool includePlaintextPassword)
        {
            var bld = new StringBuilder();

            if (!string.IsNullOrEmpty(Filename))
            {
                bld.Append("Filename=");
                AppendQuotedString(bld, Filename);
                bld.Append(';');
            }

            var fileNameLength = bld.Length;

            if (Connection != ConnectionType.Direct)
            {
                bld.Append("Connection=")
                    .Append(Connection)
                    .Append(';');
            }

            if (Password != null)
            {
                bld.Append("Password=");
                if (includePlaintextPassword)
                {
                    AppendQuotedString(bld, Password);
                }
                else
                {
                    bld.Append('*', 8);
                }

                bld.Append(';');
            }

            if (InitialSize != 0)
            {
                bld.Append("InitialSize=")
                    .AppendFormat(CultureInfo.InvariantCulture, "{0:D}", InitialSize)
                    .Append(';');
            }

            if (ReadOnly)
            {
                bld.Append("ReadOnly=")
                    .Append(ReadOnly)
                    .Append(';');
            }

            if (Collation != null)
            {
                bld.Append("Collation=")
                    .Append(Collation)
                    .Append(';');
            }

            if (Upgrade)
            {
                bld.Append("Upgrade=")
                    .Append(Upgrade)
                    .Append(';');
            }

            if (AutoRebuild)
            {
                bld.Append("Auto-Rebuild=")
                    .Append(AutoRebuild)
                    .Append(';');
            }

            if (RejectInvalidLocalTime)
            {
                bld.Append("Reject Invalid Local Time=")
                    .Append(RejectInvalidLocalTime)
                    .Append(';');
            }

            if (CompactStorage) bld.Append("Compact Storage=true;");

            if (DurableCommits == false)
            {
                bld.Append("Durable Commits=")
                    .Append(DurableCommits)
                    .Append(';');
            }

            if (MemoryProfile != MemoryProfile.Balanced)
            {
                bld.Append("Memory Profile=")
                    .Append(MemoryProfile)
                    .Append(';');
            }

            if (CacheSize != 0)
            {
                bld.Append("Cache Size=")
                    .AppendFormat(CultureInfo.InvariantCulture, "{0:D}", CacheSize)
                    .Append(';');
            }

            if (_transactionPageLimit.HasValue)
            {
                bld.Append("Transaction Pages=")
                    .AppendFormat(CultureInfo.InvariantCulture, "{0:D}", _transactionPageLimit.Value)
                    .Append(';');
            }

            if (bld.Length == fileNameLength &&
                !string.IsNullOrEmpty(Filename) &&
                Filename == Filename.Trim() &&
                !Filename.Contains("="))
            {
                return Filename;
            }

            if (bld.Length > 0)
            {
                bld.Length--; // ;
            }

            return bld.ToString();
        }

        private static void AppendQuotedString(StringBuilder target, string str)
        {
            target.Append('"');

            var backslashCount = 0;

            foreach (var chr in str ?? string.Empty)
            {
                if (chr == '\\')
                {
                    backslashCount++;
                    continue;
                }

                target.Append('\\', chr == '"' ? backslashCount * 2 + 1 : backslashCount);
                target.Append(chr);
                backslashCount = 0;
            }

            target.Append('\\', backslashCount * 2);
            target.Append('"');
        }
    }
}
