using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Header page represent first page on datafile. Engine contains a single instance of HeaderPage and all changes
    /// must be synchronized (using lock).
    /// </summary>
    internal class HeaderPage : BasePage
    {
        /// <summary>
        /// Header info the validate that datafile is a LiteDB file (27 bytes)
        /// </summary>
        public const string HEADER_INFO = "** This is a LiteDB file **";

        /// <summary>
        /// Datafile specification version
        /// </summary>
        public const byte FILE_VERSION = 8;

        #region Buffer Field Positions

        public const int P_HEADER_INFO = 32;  // 32-58 (27 bytes)
        public const int P_FILE_VERSION = 59; // 59-59 (1 byte)
        public const int P_FREE_EMPTY_PAGE_ID = 60; // 60-63 (4 bytes)
        public const int P_LAST_PAGE_ID = 64; // 64-67 (4 bytes)
        public const int P_CREATION_TIME = 68; // 68-75 (8 bytes)

        // private const int P_PRAGMAS = 76; // 76-190 (115 bytes)
        public const int P_INVALID_DATAFILE_STATE = 191; // 191-191 (1 byte)

        public const int P_COLLECTIONS = 192; // 192-8159 (8064 bytes)
        public const int COLLECTIONS_SIZE = 8000; // 250 blocks with 32 bytes each

        #endregion

        /// <summary>
        /// Get/Set the pageID that start sequence with a complete empty pages (can be used as a new page) [4 bytes]
        /// </summary>
        public uint FreeEmptyPageList { get; set; }

        /// <summary>
        /// Last created page - Used when there is no free page inside file [4 bytes]
        /// </summary>
        public uint LastPageID { get; set; }

        /// <summary>
        /// DateTime when database was created [8 bytes]
        /// </summary>
        public DateTime CreationTime { get; }

        /// <summary>
        /// Get database pragmas instance class
        /// </summary>
        public EnginePragmas Pragmas { get; set; }

        /// <summary>
        /// All collections names/link pointers are stored inside this document
        /// </summary>
        private BsonDocument _collections;

        /// <summary>
        /// Check if collections was changed
        /// </summary>
        private bool _isCollectionsChanged = false;

        /// <summary>
        /// Create new Header Page
        /// </summary>
        public HeaderPage(PageBuffer buffer, uint pageID)
            : base(buffer, 0, PageType.Header)
        {
            // initialize page version
            this.CreationTime = DateTime.UtcNow;
            this.FreeEmptyPageList = uint.MaxValue;
            this.LastPageID = 0;

            // initialize pragmas
            this.Pragmas = new EnginePragmas(this);

            // writing direct into buffer in Ctor() because there is no change later (write once)
            _buffer.Write(HEADER_INFO, P_HEADER_INFO);
            _buffer.Write(FILE_VERSION, P_FILE_VERSION);
            _buffer.Write(this.CreationTime, P_CREATION_TIME);

            // initialize collections
            _collections = new BsonDocument();
        }

        /// <summary>
        /// Load HeaderPage from buffer page
        /// </summary>
        public HeaderPage(PageBuffer buffer)
            : base(buffer)
        {
            this.CreationTime = _buffer.ReadDateTime(P_CREATION_TIME);

            this.LoadPage();
        }

        /// <summary>
        /// Load page content based on page buffer
        /// </summary>
        private void LoadPage()
        {
            // check database file format
            var info = _buffer.ReadString(P_HEADER_INFO, HEADER_INFO.Length);
            var ver = _buffer[P_FILE_VERSION];

            if (string.CompareOrdinal(info, HEADER_INFO) != 0 || ver != FILE_VERSION)
            {
                throw LiteException.InvalidDatabase();
            }

            // CreateTime is readonly
            this.FreeEmptyPageList = _buffer.ReadUInt32(P_FREE_EMPTY_PAGE_ID);
            this.LastPageID = _buffer.ReadUInt32(P_LAST_PAGE_ID);

            // initialize engine pragmas
            this.Pragmas = new EnginePragmas(_buffer, this);

            // create new buffer area to store BsonDocument collections
            var area = _buffer.Slice(P_COLLECTIONS, COLLECTIONS_SIZE);

            using (var r = new BufferReader(new[] { area }, false))
            {
                _collections = r.ReadDocument().GetValue();
            }

            _isCollectionsChanged = false;
        }

        public override PageBuffer UpdateBuffer()
        {
            _buffer.Write(this.FreeEmptyPageList, P_FREE_EMPTY_PAGE_ID);
            _buffer.Write(this.LastPageID, P_LAST_PAGE_ID);

            // update engine pragmas
            this.Pragmas.UpdateBuffer(_buffer);

            // update collection only if needed
            if (_isCollectionsChanged)
            {
                var area = _buffer.Slice(P_COLLECTIONS, COLLECTIONS_SIZE);

                using (var w = new BufferWriter(area))
                {
                    w.WriteDocument(_collections, true);
                }

                _isCollectionsChanged = false;
            }

            return base.UpdateBuffer();
        }

        /// <summary>
        /// Create a save point before do any change on header page (execute UpdateBuffer())
        /// </summary>
        public PageBuffer Savepoint()
        {
            this.UpdateBuffer();

            var savepoint = new PageBuffer(new byte[PAGE_SIZE], 0, 0);

            System.Buffer.BlockCopy(_buffer.Array, _buffer.Offset, savepoint.Array, savepoint.Offset, PAGE_SIZE);

            return savepoint;
        }

        /// <summary>
        /// Restore savepoint content and override on page. Must run in lock(_header)
        /// </summary>
        public void Restore(PageBuffer savepoint)
        {
            System.Buffer.BlockCopy(savepoint.Array, savepoint.Offset, _buffer.Array, _buffer.Offset, PAGE_SIZE);

            this.LoadPage();
        }

        /// <summary>
        /// Get collection PageID - return uint.MaxValue if not exists
        /// </summary>
        public uint GetCollectionPageID(string collection)
        {
            if (_collections.TryGetValue(collection, out var pageID))
            {
                return (uint)pageID.AsInt32;
            }

            return uint.MaxValue;
        }

        /// <summary>
        /// Get all collections with pageID
        /// </summary>
        public IEnumerable<KeyValuePair<string, uint>> GetCollections()
        {
            foreach(var el in _collections.GetElements())
            {
                yield return new KeyValuePair<string, uint>(el.Key, (uint)el.Value.AsInt32);
            }
        }

        /// <summary>
        /// Insert new collection in header
        /// </summary>
        public void InsertCollection(string name, uint pageID)
        {
            _collections[name] = (int)pageID;

            _isCollectionsChanged = true;
        }

        /// <summary>
        /// Remove existing collection reference in header
        /// </summary>
        public void DeleteCollection(string name)
        {
            _collections.Remove(name);

            _isCollectionsChanged = true;
        }

        /// <summary>
        /// Rename collection with new name
        /// </summary>
        public void RenameCollection(string oldName, string newName)
        {
            var pageID = _collections[oldName];

            _collections.Remove(oldName);

            _collections.Add(newName, pageID);

            _isCollectionsChanged = true;
        }

        /// <summary>
        /// Get how many bytes are available in collection to store new collections
        /// </summary>
        public int GetAvailableCollectionSpace()
        {
            return COLLECTIONS_SIZE -
                _collections.GetBytesCount(true) -
                1 - // for int32 type (0x10)
                1 - // for new CString ('\0')
                4 - // for PageID (int32)
                8; // reserved
        }
    }
}