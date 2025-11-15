using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Globalization;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Manages connection string parsing and configuration for connecting to and creating LiteDB databases.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Connection strings use a name-value pair format: <c>Name1=Value1; Name2=Value2</c>.</para>
    /// <para>Alternatively, you can provide just a filename without the key-value format (e.g., <c>"mydata.db"</c>).</para>
    /// <para>Supported connection string parameters:</para>
    /// <list type="table">
    /// <listheader>
    /// <term>Parameter</term>
    /// <description>Description</description>
    /// </listheader>
    /// <item>
    /// <term>filename</term>
    /// <description>Full path or relative path from DLL directory to the database file.</description>
    /// </item>
    /// <item>
    /// <term>connection</term>
    /// <description>Connection type: Direct or Shared (default: Direct).</description>
    /// </item>
    /// <item>
    /// <term>password</term>
    /// <description>Password for encrypting/decrypting data pages.</description>
    /// </item>
    /// <item>
    /// <term>initial size</term>
    /// <description>Allocated space for new databases. Supports KB, MB, GB suffixes (default: 0).</description>
    /// </item>
    /// <item>
    /// <term>readonly</term>
    /// <description>Open database in read-only mode (default: <see langword="false"/>).</description>
    /// </item>
    /// <item>
    /// <term>upgrade</term>
    /// <description>Convert old database versions before opening (default: <see langword="false"/>).</description>
    /// </item>
    /// <item>
    /// <term>auto-rebuild</term>
    /// <description>Rebuild database on next open if previous close resulted in invalid state (default: <see langword="false"/>).</description>
    /// </item>
    /// <item>
    /// <term>collation</term>
    /// <description>Default collation for database creation (default: current culture/IgnoreCase).</description>
    /// </item>
    /// </list>
    /// </remarks>
    public class ConnectionString
    {
        private readonly Dictionary<string, string> _values;

        /// <summary>
        /// Gets or sets the connection type determining how the engine will be opened.
        /// <para>Default is <see cref="ConnectionType.Direct"/>.</para>
        /// </summary>
        public ConnectionType Connection { get; set; } = ConnectionType.Direct;

        /// <summary>
        /// Gets or sets the database filename. Can be a full path or relative path from the DLL directory.
        /// </summary>
        public string Filename { get; set; } = "";

        /// <summary>
        /// Gets or sets the database password used to encrypt/decrypt data pages. <see langword="null"/> means no encryption.
        /// </summary>
        public string Password { get; set; } = null;

        /// <summary>
        /// Gets or sets the initial allocated space for new databases. Supports KB, MB, and GB suffixes.
        /// <para>Default is 0 (no pre-allocation).</para>
        /// </summary>
        public long InitialSize { get; set; } = 0;

        /// <summary>
        /// Gets or sets whether to open the database in read-only mode.
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool ReadOnly { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to check for and convert old database versions before opening.
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool Upgrade { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to automatically rebuild the database on next open if the previous close resulted in an invalid data state.
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool AutoRebuild { get; set; } = false;

        /// <summary>
        /// Gets or sets the default collation for database creation. If not specified, uses the current culture with case-insensitive comparison.
        /// </summary>
        public Collation Collation { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionString"/> class with default values.
        /// </summary>
        public ConnectionString()
        {
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionString"/> class by parsing a connection string.
        /// </summary>
        /// <param name="connectionString">
        /// The connection string in <c>key1=value1;key2=value2</c> format, or just a filename if no semicolon is present.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connectionString"/> is <see langword="null"/> or empty.</exception>
        public ConnectionString(string connectionString)
            : this()
        {
            // TODO: wouldn't it be better to throw ArgumentException since the argument might be empty instead of always null?
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
            this.ReadOnly = _values.GetValue("readonly", this.ReadOnly);

            this.Collation = _values.ContainsKey("collation") ? new Collation(_values.GetValue<string>("collation")) : this.Collation;

            this.Upgrade = _values.GetValue("upgrade", this.Upgrade);
            this.AutoRebuild = _values.GetValue("auto-rebuild", this.AutoRebuild);
        }

        /// <summary>
        /// Gets the value associated with the specified key from the parsed connection string.
        /// </summary>
        /// <param name="key">The connection string parameter name (case-insensitive).</param>
        /// <returns>The value associated with the key, or <see langword="null"/> if the key is not found.</returns>
        public string this[string key] => _values.GetOrDefault(key);

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