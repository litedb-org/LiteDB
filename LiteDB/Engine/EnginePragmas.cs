using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Internal database pragmas persisted inside header page
    /// </summary>
    internal class EnginePragmas
    {
        // pragma reserved area: 76-190 (115 bytes)

        public const int P_USER_VERSION = 76; // 76-79 (4 bytes)
        public const int P_COLLATION_LCID = 80; // 80-83 (4 bytes)
        public const int P_COLLATION_SORT = 84; // 84-87 (4 bytes)
        public const int P_TIMEOUT = 88; // 88-91 (4 bytes)
        public const int P_COLLATION_STAMP = 92; // 92-95 (4 bytes)
        public const int P_UTC_DATE = 96; // 96-96 (1 byte)
        public const int P_CHECKPOINT = 97; // 97-100 (4 bytes)
        public const int P_LIMIT_SIZE = 101; // 101-108 (8 bytes)
        public const int P_INDEX_ORDER_VERSION = 165; // 165 (1 byte)
        internal const byte INDEX_ORDER_VERSION = 1;

        /// <summary>
        /// Internal user version control to detect database changes
        /// </summary>
        public int UserVersion { get; private set; } = 0;

        /// <summary>
        /// Define collation for this database. Value will be persisted on disk at first write database. After this, there is no change of collation
        /// </summary>
        public Collation Collation { get; private set; } = Collation.Default;

        /// <summary>
        /// Timeout for waiting unlock operations (default: 1 minute)
        /// </summary>
        public TimeSpan Timeout { get; private set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Max limit of datafile (in bytes) (default: MaxValue)
        /// </summary>
        public long LimitSize { get; private set; } = long.MaxValue;

        /// <summary>
        /// Returns date in UTC timezone from BSON deserialization (default: false == LocalTime)
        /// </summary>
        public bool UtcDate { get; private set; } = false;

        /// <summary>
        /// When LOG file gets larger than checkpoint size (in pages), do a soft checkpoint (and also do a checkpoint at shutdown)
        /// Checkpoint = 0 means there's no auto-checkpoint nor shutdown checkpoint
        /// </summary>
        public int Checkpoint { get; private set; } = 1000;

        private readonly Dictionary<string, Pragma> _pragmas;
        private bool _isDirty = false;
        private bool _newFile = true;
        internal uint CollationStamp { get; private set; }
        internal byte IndexOrderVersion { get; private set; } = INDEX_ORDER_VERSION;

        internal void CompleteIndexMigration()
        {
            IndexOrderVersion = INDEX_ORDER_VERSION;
            CollationStamp = CollationFingerprint.Compute(Collation);
            _isDirty = true;
        }
        private readonly HeaderPage _headerPage;

        /// <summary>
        /// Get all pragmas
        /// </summary>
        public IEnumerable<Pragma> Pragmas => _pragmas.Values;

        public EnginePragmas(HeaderPage headerPage)
        {
            _headerPage = headerPage;

            _pragmas = new Dictionary<string, Pragma>(StringComparer.OrdinalIgnoreCase)
            {
                [Engine.Pragmas.USER_VERSION] = new Pragma
                {
                    Name = Engine.Pragmas.USER_VERSION,
                    Get = () => this.UserVersion,
                    Set = (v) => this.UserVersion = v.AsInt32,
                    Read = (b) => this.UserVersion = b.ReadInt32(P_USER_VERSION),
                    Validate = (v, h) => { },
                    Write = (b) => b.Write(this.UserVersion, P_USER_VERSION)
                },
                [Engine.Pragmas.COLLATION] = new Pragma
                {
                    Name = Engine.Pragmas.COLLATION,
                    Get = () => this.Collation.ToString(),
                    Set = (v) => this.Collation = new Collation(v.AsString),
                    Read = (b) =>
                    {
                        try { this.Collation = new Collation(b.ReadInt32(P_COLLATION_LCID), (CompareOptions)b.ReadInt32(P_COLLATION_SORT)); }
                        catch (CultureNotFoundException ex)
                        {
                            throw new LiteException(0, ex, "Database collation is unavailable in this globalization runtime. Rebuild in the original environment using Ordinal collation.");
                        }
                    },
                    Validate = (v, h) => { throw new LiteException(0, "Pragma COLLATION is read only. Use Rebuild options."); },
                    Write = (b) =>
                    {
                        b.Write(this.Collation.LCID, P_COLLATION_LCID);
                        b.Write((int)this.Collation.SortOptions, P_COLLATION_SORT);
                    }
                },
                [Engine.Pragmas.TIMEOUT] = new Pragma
                {
                    Name = Engine.Pragmas.TIMEOUT,
                    Get = () => (int)this.Timeout.TotalSeconds,
                    Set = (v) => this.Timeout = TimeSpan.FromSeconds(v.AsInt32),
                    Read = (b) => this.Timeout = TimeSpan.FromSeconds(b.ReadInt32(P_TIMEOUT)),
                    Validate = (v, h) => { if (v <= 0) throw new LiteException(0, "Pragma TIMEOUT must be greater than zero"); },
                    Write = (b) => b.Write((int)this.Timeout.TotalSeconds, P_TIMEOUT)
                },
                [Engine.Pragmas.LIMIT_SIZE] = new Pragma
                {
                    Name = Engine.Pragmas.LIMIT_SIZE,
                    Get = () => this.LimitSize,
                    Set = (v) => this.LimitSize = v.AsInt64,
                    Read = (b) =>
                    {
                        var limit = b.ReadInt64(P_LIMIT_SIZE);
                        this.LimitSize = limit == 0 ? long.MaxValue : limit;
                    },
                    Validate = (v, h) =>
                    {
                        if (v < 4 * PAGE_SIZE) throw new LiteException(0, "Pragma LIMIT_SIZE must be at least 4 pages (32768 bytes)");
                        if (h != null && v.AsInt64 < (h.LastPageID + 1) * Constants.PAGE_SIZE) throw new LiteException(0, "Pragma LIMIT_SIZE must be greater or equal to the current file size");
                    },
                    Write = (b) => b.Write(this.LimitSize, P_LIMIT_SIZE)
                },
                [Engine.Pragmas.UTC_DATE] = new Pragma
                {
                    Name = Engine.Pragmas.UTC_DATE,
                    Get = () => this.UtcDate,
                    Set = (v) => this.UtcDate = v.AsBoolean,
                    Read = (b) => this.UtcDate = b.ReadBool(P_UTC_DATE),
                    Validate = (v, h) => { },
                    Write = (b) => b.Write(this.UtcDate, P_UTC_DATE)
                },
                [Engine.Pragmas.CHECKPOINT] = new Pragma
                {
                    Name = Engine.Pragmas.CHECKPOINT,
                    Get = () => this.Checkpoint,
                    Set = (v) => this.Checkpoint = v.AsInt32,
                    Read = (b) => this.Checkpoint = b.ReadInt32(P_CHECKPOINT),
                    Validate = (v, h) => { if (v < 0) throw new LiteException(0, "Pragma CHECKPOINT must be greater or equal to zero"); },
                    Write = (b) => b.Write(this.Checkpoint, P_CHECKPOINT)
                }
            };

            _isDirty = true;
        }

        public EnginePragmas(BufferSlice buffer, HeaderPage headerPage)
            : this(headerPage)
        {
            foreach(var pragma in _pragmas.Values)
            {
                pragma.Read(buffer);
            }

            this.CollationStamp = buffer.ReadUInt32(P_COLLATION_STAMP);
            this.IndexOrderVersion = buffer[HeaderPage.P_FILE_VERSION] >= HeaderPage.INDEX_FILE_VERSION ? buffer.ReadByte(P_INDEX_ORDER_VERSION) : (byte)0;
            _newFile = false;
            _isDirty = false;
        }

        public void UpdateBuffer(BufferSlice buffer)
        {
            if (_isDirty == false) return;

            foreach(var pragma in _pragmas)
            {
                pragma.Value.Write(buffer);
            }

            if (_newFile) this.CollationStamp = CollationFingerprint.Compute(this.Collation);
            buffer.Write(this.CollationStamp, P_COLLATION_STAMP);
            buffer.Write(this.IndexOrderVersion, P_INDEX_ORDER_VERSION);
            _isDirty = false;
        }

        public BsonValue Get(string name)
        {
            if (_pragmas.TryGetValue(name, out var pragma))
            {
                return pragma.Get();
            }

            throw new LiteException(0, $"Pragma `{name}` not exist");
        }

        public void Set(string name, BsonValue value, bool validate)
        {
            if (_pragmas.TryGetValue(name, out var pragma))
            {
                if (validate)
                    pragma.Validate(value, _headerPage);

                pragma.Set(value);

                _isDirty = true;
            }
            else
            {
                throw new LiteException(0, $"Pragma `{name}` not exist");
            }
        }

        public void Validate(string name, BsonValue value)
        {
            if (_pragmas.TryGetValue(name, out var pragma))
            {
                pragma.Validate(value, _headerPage);
                return;
            }
            throw new LiteException(0, $"Pragma `{name}` not exist");
        }
    }
}
