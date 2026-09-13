using System;
using System.Collections.Generic;
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
        /// Initialize connection string parsing string in "key1=value1;key2=value2;...." format or only "filename" as default (when no ; char found)
        /// </summary>
        public ConnectionString(string connectionString)
            : this()
        {
            if (string.IsNullOrEmpty(connectionString)) throw new ArgumentNullException(nameof(connectionString));

            // create a dictionary from string name=value collection
            if (connectionString.Contains("="))
            {
                _values.ParseKeyValue(connectionString);
            }
            else
            {
                _values["filename"] = connectionString;
            }

            // setting values to properties
            this.Connection = _values.GetValue("connection", this.Connection);
            this.Filename = _values.GetValue("filename", this.Filename).Trim();

            this.Password = _values.GetValue("password", this.Password);

            if(this.Password == string.Empty)
            {
                this.Password = null;
            }

            this.InitialSize = _values.GetFileSize(@"initial size", this.InitialSize);
            if (_values.TryGetValue("memory profile", out var profile)) this.MemoryProfile = MemoryProfileDefaults.Parse(profile);
            this.CacheSize = _values.TryGetValue("cache size", out var cacheSizeText) ?
                ParseCacheSize(cacheSizeText) : this.CacheSize;
            if (_values.ContainsKey("transaction pages")) this.TransactionPageLimit = _values.GetValue<int>("transaction pages");

            if (this.CacheSize < 0 || this.TransactionPageLimit <= 0)
            {
                throw new LiteException(0, "`cache size` must be non-negative and `transaction pages` must be greater than zero");
            }
            this.ReadOnly = _values.GetValue("readonly", this.ReadOnly);

            this.Collation = _values.ContainsKey("collation") ? new Collation(_values.GetValue<string>("collation")) : this.Collation;

            this.Upgrade = _values.GetValue("upgrade", this.Upgrade);
            this.AutoRebuild = _values.GetValue("auto-rebuild", this.AutoRebuild);
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
                Collation = this.Collation,
                Upgrade = this.Upgrade,
                AutoRebuild = this.AutoRebuild,
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
    }
}
