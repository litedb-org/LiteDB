using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Provides configuration settings for initializing a <see cref="LiteEngine"/> instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class contains all the parameters needed to configure and start the LiteDB engine, including
    /// database file location, encryption, streams, and operational modes.
    /// </para>
    /// </remarks>
    public class EngineSettings
    {
        /// <summary>
        /// Gets or sets a custom stream to be used as the data file (can be <see cref="MemoryStream"/> or <see cref="TempStream"/>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Do not use <see cref="FileStream"/> - to use a physical file, set the <see cref="Filename"/> property instead
        /// and keep <see cref="DataStream"/> and <see cref="LogStream"/> as <see langword="null"/>.
        /// </para>
        /// <para>Default is <see langword="null"/> (use file-based storage via <see cref="Filename"/>).</para>
        /// </remarks>
        public Stream DataStream { get; set; } = null;

        /// <summary>
        /// Gets or sets a custom stream to be used as the write-ahead log file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If <see langword="null"/>, the engine will automatically create:
        /// </para>
        /// <list type="bullet">
        /// <item><description>A new <see cref="TempStream"/> if <see cref="DataStream"/> is a <see cref="TempStream"/>.</description></item>
        /// <item><description>A new <see cref="MemoryStream"/> if <see cref="DataStream"/> is a <see cref="MemoryStream"/>.</description></item>
        /// <item><description>A file-based log stream if using <see cref="Filename"/>.</description></item>
        /// </list>
        /// <para>Default is <see langword="null"/> (auto-create based on data source).</para>
        /// </remarks>
        public Stream LogStream { get; set; } = null;

        /// <summary>
        /// Gets or sets a custom stream to be used for temporary file operations (e.g., sorting).
        /// </summary>
        /// <remarks>
        /// <para>
        /// If <see langword="null"/>, the engine will create a new file with "-tmp" suffix in the filename.
        /// </para>
        /// <para>Default is <see langword="null"/> (auto-create temporary stream).</para>
        /// </remarks>
        public Stream TempStream { get; set; } = null;

        /// <summary>
        /// Gets or sets the database filename. Can be a full path, relative path from DLL directory, <c>:temp:</c> for temporary database, or <c>:memory:</c> for in-memory database.
        /// </summary>
        /// <remarks>
        /// <para>Special values:</para>
        /// <list type="bullet">
        /// <item><description><c>:memory:</c> - Creates an in-memory database using <see cref="MemoryStream"/>.</description></item>
        /// <item><description><c>:temp:</c> - Creates a temporary database using <see cref="TempStream"/>.</description></item>
        /// </list>
        /// <para>Default is <see langword="null"/>.</para>
        /// </remarks>
        public string Filename { get; set; }

        /// <summary>
        /// Gets or sets the database password used to encrypt and decrypt data pages.
        /// </summary>
        /// <remarks>
        /// When set, the database will use AES encryption for all data pages. Leave as <see langword="null"/> for no encryption.
        /// </remarks>
        public string Password { get; set; }

        /// <summary>
        /// Gets or sets the initial allocated space in bytes for new databases. Default is 0 (no pre-allocation).
        /// </summary>
        /// <remarks>
        /// Pre-allocating space can improve performance by reducing file system fragmentation.
        /// This setting only applies when creating a new database.
        /// </remarks>
        public long InitialSize { get; set; } = 0;

        /// <summary>
        /// Gets or sets the collation used for string comparison in the database.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This setting is only used when creating a new database. Existing databases retain their original collation.
        /// </para>
        /// <para>Default is <see cref="LiteDB.Collation.Default"/> (current culture with case-insensitive comparison).</para>
        /// </remarks>
        public Collation Collation { get; set; }

        /// <summary>
        /// Gets or sets whether to open the database in read-only mode. Default is <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// When <see langword="true"/>, the database will not support any modifications (INSERT, UPDATE, DELETE operations will fail).
        /// </remarks>
        public bool ReadOnly { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to automatically rebuild the database on next open if the previous close resulted in an exception. Default is <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If the database was not properly closed (e.g., due to an exception), the engine can automatically rebuild it
        /// from the write-ahead log on the next open.
        /// </para>
        /// <para>A backup of the original file will be created before rebuilding.</para>
        /// </remarks>
        public bool AutoRebuild { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to automatically upgrade a v4 database to v5 format on open. Default is <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When <see langword="true"/>, if an older version (v4) database is detected, it will be automatically upgraded to the new v5 format.
        /// </para>
        /// <para>A backup file will be kept in the same directory with a "-backup" suffix.</para>
        /// </remarks>
        public bool Upgrade { get; set; } = false;

        /// <summary>
        /// Gets or sets a transformation function applied to <see cref="BsonValue"/> instances when reading from the database.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This can be used to upgrade or transform data from older database versions during read operations.
        /// The function receives the field name and the original <see cref="BsonValue"/>, and returns the transformed value.
        /// </para>
        /// <para>Default is <see langword="null"/> (no transformation).</para>
        /// </remarks>
        public Func<string, BsonValue, BsonValue> ReadTransform { get; set; }
        
        /// <summary>
        /// Gets or sets the strategy for generating mutex names in shared mode.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This setting controls how mutex names are generated for shared database access across multiple processes.
        /// </para>
        /// <para>
        /// The default value is <see cref="SharedMutexNameStrategy.Default"/>.
        /// </para>
        /// <para>
        /// Use <see cref="SharedMutexNameStrategy.Default"/> for standard behavior, which is suitable for most applications.
        /// Use <see cref="SharedMutexNameStrategy.Compatibility"/> if you need to maintain compatibility with older versions or specific deployment scenarios.
        /// Refer to <see cref="SharedMutexNameStrategy"/> for details on available strategies.
        /// </para>
        /// </remarks>
        public SharedMutexNameStrategy SharedMutexNameStrategy { get; set; }

        /// <summary>
        /// Create new IStreamFactory for datafile
        /// </summary>
        internal IStreamFactory CreateDataFactory(bool useAesStream = true)
        {
            if (this.DataStream != null)
            {
                return new StreamFactory(this.DataStream, this.Password);
            }
            else if (this.Filename == ":memory:")
            {
                return new StreamFactory(new MemoryStream(), this.Password);
            }
            else if (this.Filename == ":temp:")
            {
                return new StreamFactory(new TempStream(), this.Password);
            }
            else if (!string.IsNullOrEmpty(this.Filename))
            {
                return new FileStreamFactory(this.Filename, this.Password, this.ReadOnly, false, useAesStream);
            }

            throw new ArgumentException("EngineSettings must have Filename or DataStream as data source");
        }

        /// <summary>
        /// Create new IStreamFactory for logfile
        /// </summary>
        internal IStreamFactory CreateLogFactory()
        {
            if (this.LogStream != null)
            {
                return new StreamFactory(this.LogStream, this.Password);
            }
            else if (this.Filename == ":memory:")
            {
                return new StreamFactory(new MemoryStream(), this.Password);
            }
            else if (this.Filename == ":temp:")
            {
                return new StreamFactory(new TempStream(), this.Password);
            }
            else if (!string.IsNullOrEmpty(this.Filename))
            {
                var logName = FileHelper.GetLogFile(this.Filename);

                return new FileStreamFactory(logName, this.Password, this.ReadOnly, false);
            }

            return new StreamFactory(new MemoryStream(), this.Password);
        }

        /// <summary>
        /// Create new IStreamFactory for temporary file (sort)
        /// </summary>
        internal IStreamFactory CreateTempFactory()
        {
            if (this.TempStream != null)
            {
                return new StreamFactory(this.TempStream, this.Password);
            }
            else if (this.Filename == ":memory:")
            {
                return new StreamFactory(new MemoryStream(), this.Password);
            }
            else if (this.Filename == ":temp:")
            {
                return new StreamFactory(new TempStream(), this.Password);
            }
            else if (!string.IsNullOrEmpty(this.Filename))
            {
                var tempName = FileHelper.GetTempFile(this.Filename);

                return new FileStreamFactory(tempName, this.Password, false, true);
            }

            return new StreamFactory(new TempStream(), this.Password);
        }
    }
}