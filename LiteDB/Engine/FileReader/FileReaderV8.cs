using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Internal class to read all datafile documents - use only Stream - no cache system. Read log file (read commited transtraction)
    /// </summary>
    internal class FileReaderV8 : IFileReader
    {
        private struct PageInfo
        {
            public uint PageID;
            public FileOrigin Origin;
            public PageType PageType;
            public long Position;
            public uint ColID;
        }

        private readonly Dictionary<string, uint> _collections = new Dictionary<string, uint>();
        private readonly Dictionary<string, List<IndexInfo>> _indexes = new Dictionary<string, List<IndexInfo>>();
        private readonly Dictionary<uint, List<uint>> _collectionsDataPages = new Dictionary<uint, List<uint>>();
        private readonly Dictionary<string, BsonValue> _pragmas = new Dictionary<string, BsonValue>
        {
            [Pragmas.USER_VERSION] = 0,
            [Pragmas.CHECKPOINT] = 1000,
            [Pragmas.TIMEOUT] = 60,
            [Pragmas.UTC_DATE] = true,
            [Pragmas.LIMIT_SIZE] = long.MaxValue,
        };

        private Stream _dataStream;
        private Stream _logStream;
        private readonly IDictionary<uint, long> _logIndexMap = new Dictionary<uint, long>();
        private uint _maxPageID; // a file-length based max pageID to be tested

        private bool _disposed;

        private readonly EngineSettings _settings;
        private readonly IList<FileReaderError> _errors;

        public FileReaderV8(EngineSettings settings, IList<FileReaderError> errors)
        {
            _settings = settings;
            _errors = errors;
        }

        /// <summary>
        /// Open data file and log file, read header and collection pages
        /// </summary>
        public void Open()
        {
            try
            {
                var dataFactory = _settings.CreateDataFactory();
                var logFactory = _settings.CreateLogFactory();

                // get maxPageID based on both file length
                _maxPageID = (uint)((dataFactory.GetLength() + logFactory.GetLength()) / PAGE_SIZE);

                _dataStream = dataFactory.GetStream(true, false);

                _dataStream.Position = 0;

                if (logFactory.Exists())
                {
                    _logStream = logFactory.GetStream(false, true);

                    this.LoadIndexMap();
                }

                this.LoadPragmas();

                this.LoadDataPages();

                this.LoadCollections();

                this.LoadIndexes();
            }
            catch (Exception ex)
            {
                this.HandleError(ex, new PageInfo());
            }
        }

        /// <summary>
        /// Read all pragma values
        /// </summary>
        public IDictionary<string, BsonValue> GetPragmas() => _pragmas;

        /// <summary>
        /// Read all collection based on header page
        /// </summary>
        public IEnumerable<string> GetCollections() => _collections.Keys;

        /// <summary>
        /// Read all indexes from all collection pages (except _id index)
        /// </summary>
        public IEnumerable<IndexInfo> GetIndexes(string collection) => _indexes.ContainsKey(collection) ? _indexes[collection] : new List<IndexInfo>();

        /// <summary>
        /// Read all documents from current collection with NO index use - read direct from free lists
        /// There is no document order
        /// </summary>
        public IEnumerable<BsonDocument> GetDocuments(string collection)
        {
            if (!_collections.ContainsKey(collection)) yield break;

            var colID = _collections[collection];

            if (!_collectionsDataPages.ContainsKey(colID)) yield break;

            var dataPages = _collectionsDataPages[colID];
            var uniqueIDs = new HashSet<BsonValue>();

            foreach (var dataPage in dataPages)
            {
                var page = this.ReadPage(dataPage, out var pageInfo);

                if (page.Fail)
                {
                    this.HandleError(page.Exception, pageInfo);
                    continue;
                }

                var buffer = page.Value.Buffer;
                var itemsCount = page.Value.ItemsCount;
                var highestIndex = page.Value.HighestIndex;

                // no items
                if (itemsCount == 0 || highestIndex == byte.MaxValue) continue;

                for (int i = 0; i <= highestIndex; i++)
                {
                    BsonDocument doc;

                    // try/catch block per dataBlock extend=false
                    try
                    {
                        // resolve slot address
                        var positionAddr = BasePage.CalcPositionAddr((byte)i);
                        var lengthAddr = BasePage.CalcLengthAddr((byte)i);

                        // read segment position/length
                        var position = buffer.ReadUInt16(positionAddr);
                        var length = buffer.ReadUInt16(lengthAddr);

                        // empty slot
                        if (position == 0) continue;

                        ENSURE(position > 0 && length > 0, "Invalid footer ref position {0} with length {1}", position, length);
                        ENSURE(position + length < PAGE_SIZE, "Invalid footer ref position {0} with length {1}", position, length);

                        // get segment slice
                        var segment = buffer.Slice(position, length);
                        var extend = segment.ReadBool(DataBlock.P_EXTEND);
                        var nextBlock = segment.ReadPageAddress(DataBlock.P_NEXT_BLOCK);
                        var data = segment.Slice(DataBlock.P_BUFFER, segment.Count - DataBlock.P_BUFFER);

                        if (extend) continue; // ignore extend block (start only in first data block)

                        // merge all data block content into a single memory stream and read bson document
                        using (var mem = new MemoryStream())
                        {
                            // write first block
                            mem.Write(data.Array, data.Offset, data.Count);

                            while (nextBlock.IsEmpty == false)
                            {
                                // read next page block
                                var nextPage = this.ReadPage(nextBlock.PageID, out pageInfo);

                                if (nextPage.Fail) throw nextPage.Exception;

                                var nextBuffer = nextPage.Value.Buffer;

                                // make page validations
                                ENSURE(nextPage.Value.PageType == PageType.Data, "Invalid PageType (excepted Data, get {0})", nextPage.Value.PageType);
                                ENSURE(nextPage.Value.ColID == colID, "Invalid ColID in this page (expected {0}, get {1})", colID, nextPage.Value.ColID);
                                ENSURE(nextPage.Value.ItemsCount > 0, "Page with no items count");

                                // read slot address
                                positionAddr = BasePage.CalcPositionAddr(nextBlock.Index);
                                lengthAddr = BasePage.CalcLengthAddr(nextBlock.Index);

                                // read segment position/length
                                position = nextBuffer.ReadUInt16(positionAddr);
                                length = nextBuffer.ReadUInt16(lengthAddr);

                                // empty slot
                                ENSURE(length > 0, "Last DataBlock request a next extend to {0}, but this block are empty footer", nextBlock);

                                // get segment slice
                                segment = nextBuffer.Slice(position, length);
                                extend = segment.ReadBool(DataBlock.P_EXTEND);
                                nextBlock = segment.ReadPageAddress(DataBlock.P_NEXT_BLOCK);
                                data = segment.Slice(DataBlock.P_BUFFER, segment.Count - DataBlock.P_BUFFER);

                                ENSURE(extend == true, "Next datablock always be an extend. Invalid data block {0}", nextBlock);

                                // write data on memorystream

                                mem.Write(data.Array, data.Offset, data.Count);
                            }

                            var docBytes = mem.ToArray();

                            // read all data array in bson document
                            using (var r = new BufferReader(docBytes, false))
                            {
                                var docResult = r.ReadDocument();
                                var id = docResult.Value["_id"];

                                ENSURE(!(id == BsonValue.Null || id == BsonValue.MinValue || id == BsonValue.MaxValue), "Invalid _id value: {0}", id);
                                ENSURE(uniqueIDs.Contains(id) == false, "Duplicated _id value: {0}", id);

                                uniqueIDs.Add(id);

                                if (docResult.Fail)
                                {
                                    this.HandleError(docResult.Exception, pageInfo);
                                }

                                doc = docResult.Value;
                            }
                        }
                    }
                    // try/catch block per dataBlock extend=false
                    catch (Exception ex)
                    {
                        this.HandleError(ex, pageInfo);
                        doc = null;
                    }

                    if (doc != null)
                    {
                        yield return doc;
                    }
                }
            }
        }

        /// <summary>
        /// Load all pragmas from header page
        /// </summary>
        private void LoadPragmas()
        {
            var result = this.ReadPage(0, out var pageInfo);

            if (result.Ok)
            {
                var buffer = result.Value.Buffer;

                _pragmas[Pragmas.USER_VERSION] = buffer.ReadInt32(EnginePragmas.P_USER_VERSION);
                _pragmas[Pragmas.CHECKPOINT] = buffer.ReadInt32(EnginePragmas.P_CHECKPOINT);
                _pragmas[Pragmas.TIMEOUT] = buffer.ReadInt32(EnginePragmas.P_TIMEOUT);
                _pragmas[Pragmas.UTC_DATE] = buffer.ReadBool(EnginePragmas.P_UTC_DATE);
                _pragmas[Pragmas.LIMIT_SIZE] = buffer.ReadInt64(EnginePragmas.P_LIMIT_SIZE);
            }
            else
            {
                this.HandleError(result.Exception, pageInfo);
            }
        }

        /// <summary>
        /// Read all file (and log) to find all data pages (and store groupby colPageID)
        /// </summary>
        private void LoadDataPages()
        {
            var header = this.ReadPage(0, out var pageInfo).GetValue();
            var lastPageID = header.Buffer.ReadUInt32(HeaderPage.P_LAST_PAGE_ID); //TOFO: tentar não usar esse valor como referencia (varrer tudo)

            ENSURE(lastPageID <= _maxPageID, "LastPageID {0} should be less or equals to maxPageID {1}", lastPageID, _maxPageID);

            for (uint i = 0; i <= lastPageID; i++)
            {
                var result = this.ReadPage(i, out pageInfo);

                if (result.Ok)
                {
                    var page = result.Value;

                    if (page.PageType == PageType.Data)
                    {
                        if (_collectionsDataPages.TryGetValue(page.ColID, out var list))
                        {
                            list.Add(page.PageID);
                        }
                        else
                        {
                            _collectionsDataPages[page.ColID] = new List<uint> { page.PageID };
                        }
                    }
                }
                else
                {
                    this.HandleError(result.Exception, pageInfo);
                }
            }
        }

        /// <summary>
        /// Load all collections from header OR via all data-pages ColID
        /// </summary>
        private void LoadCollections()
        {
            var header = this.ReadPage(0, out var pageInfo).GetValue();

            var area = header.Buffer.Slice(HeaderPage.P_COLLECTIONS, HeaderPage.COLLECTIONS_SIZE);

            using (var r = new BufferReader(new[] { area }, false))
            {
                var result = r.ReadDocument();

                // can't be fully read
                var collections = result.Value;

                foreach (var key in collections.Keys)
                {
                    // collections.key = collection name
                    // collections.value = collection PageID
                    var colID = collections[key];

                    if (colID.IsNumber == false)
                    {
                        this.HandleError($"ColID expect a number but get {colID}", pageInfo);
                    }
                    else
                    {
                        _collections[key] = (uint)collections[key].AsInt32;
                    }
                }

                if (result.Fail)
                {
                    this.HandleError(result.Exception, pageInfo);
                }
            }

            // for each collection loaded by datapages, check if exists in _collections
            foreach(var collection in _collectionsDataPages)
            {
                if (!_collections.ContainsValue(collection.Key))
                {
                    _collections["col_" + collection.Key] = collection.Key;
                }
            }
        }

        /// <summary>
        /// Load all indexes for all collections
        /// </summary>
        private void LoadIndexes()
        {
            foreach (var collection in _collections)
            {
                var result = this.ReadPage(collection.Value, out var pageInfo);

                if (result.Fail)
                {
                    this.HandleError(result.Exception, pageInfo);
                    continue;
                }

                var page = result.Value;
                var buffer = page.Buffer;

                var count = buffer.ReadByte(CollectionPage.P_INDEXES); // 1 byte
                var position = CollectionPage.P_INDEXES + 1;

                // handle error per collection
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        position += 2; // skip: slot (1 byte) + indexType (1 byte)

                        var name = buffer.ReadCString(position, out var nameLength);

                        position += nameLength;

                        var expr = buffer.ReadCString(position, out var exprLength);

                        position += exprLength;

                        var unique = buffer.ReadBool(position);

                        position++;

                        position += 15; // head 5 bytes, tail 5 bytes, reserved 1 byte, freeIndexPageList 4 bytes

                        ENSURE(!string.IsNullOrEmpty(name), "Index name can't be empty (collection {0} - index: {1})", collection.Key, i);
                        ENSURE(!string.IsNullOrEmpty(expr), "Index expression can't be empty (collection {0} - index: {1})", collection.Key, i);

                        var indexInfo = new IndexInfo
                        {
                            Collection = collection.Key,
                            Name = name,
                            Expression = expr,
                            Unique = unique
                        };

                        // ignore _id index
                        if (name == "_id") continue;

                        if (_indexes.TryGetValue(collection.Key, out var indexInfos))
                        {
                            indexInfos.Add(indexInfo);
                        }
                        else
                        {
                            _indexes[collection.Key] = new List<IndexInfo> { indexInfo };
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.HandleError(ex, pageInfo);
                    continue;
                }
            }
        }

        /// <summary>
        /// Check header slots to test if data file is a LiteDB FILE_VERSION = v8
        /// </summary>
        public static bool IsVersion(byte[] buffer)
        {
            var header = Encoding.UTF8.GetString(buffer, HeaderPage.P_HEADER_INFO, HeaderPage.HEADER_INFO.Length);
            var version = buffer[HeaderPage.P_FILE_VERSION];

            // buffer[0] = 1 when datafile is encrypted (this feature was added in v8 only)
            // all other version has this buffer[0] = 0

            return (header == HeaderPage.HEADER_INFO && version == HeaderPage.FILE_VERSION) ||
                buffer[0] == 1;
        }

        /// <summary>
        /// Load log file to build index map (wal map index)
        /// </summary>
        private void LoadIndexMap()
        {
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            var transactions = new Dictionary<uint, List<PagePosition>>();
            var confirmedTransactions = new List<uint>();
            var currentPosition = 0L;
            var pageInfo = new PageInfo { Origin = FileOrigin.Log };

            _logStream.Position = 0;

            while (_logStream.Position < _logStream.Length)
            {
                try
                {
                    _logStream.Position = pageInfo.Position = currentPosition;

                    var read = _logStream.Read(buffer.Array, buffer.Offset, PAGE_SIZE);

                    if (buffer.IsBlank())
                    {
                        // this should not happen, but if it does, it means there's a zeroed page in the file
                        // just skip it
                        currentPosition += PAGE_SIZE;
                        continue;
                    }

                    var pageID = buffer.ReadUInt32(BasePage.P_PAGE_ID);
                    var isConfirmed = buffer.ReadBool(BasePage.P_IS_CONFIRMED);
                    var transactionID = buffer.ReadUInt32(BasePage.P_TRANSACTION_ID);

                    pageInfo.PageID = pageID;
                    pageInfo.ColID = buffer.ReadUInt32(BasePage.P_COL_ID);

                    ENSURE(read == PAGE_SIZE, "Page position {0} read only than {1} bytes (instead {2})", _logStream, read, PAGE_SIZE);

                    var position = new PagePosition(pageID, currentPosition);

                    if (transactions.TryGetValue(transactionID, out var list))
                    {
                        list.Add(position);
                    }
                    else
                    {
                        transactions[transactionID] = new List<PagePosition> { position };
                    }

                    // when page confirm transaction, add to confirmed transaction list
                    if (isConfirmed)
                    {
                        confirmedTransactions.Add(transactionID);
                    }
                }
                catch (Exception ex)
                {
                    this.HandleError(ex, pageInfo);
                }
                finally
                {
                    currentPosition += PAGE_SIZE;
                }
            }

            // now, log index map using only confirmed transactions (override with last transactionID)
            foreach (var transactionID in confirmedTransactions)
            {
                var mapIndexPages = transactions[transactionID];

                // update
                foreach (var page in mapIndexPages)
                {
                    _logIndexMap[page.PageID] = page.Position;
                }
            }
        }

        /// <summary>
        /// Read page from data/log stream (checks in logIndexMap file/position). Capture any exception here, but don't call HandleError
        /// </summary>
        private Result<BasePage> ReadPage(uint pageID, out PageInfo pageInfo)
        {
            pageInfo = new PageInfo { PageID = pageID };

            try
            {
                ENSURE(pageID <= _maxPageID, "PageID: {0} should be less then or equals to maxPageID: {1}", pageID, _maxPageID);

                var pageBuffer = new PageBuffer(new byte[PAGE_SIZE], 0, PAGE_SIZE);
                Stream stream;
                int read;

                // get data from log file or original file
                if (_logIndexMap.TryGetValue(pageID, out pageInfo.Position))
                {
                    pageInfo.Origin = FileOrigin.Log;
                    stream = _logStream;
                }
                else
                {
                    pageInfo.Origin = FileOrigin.Data;
                    pageInfo.Position = BasePage.GetPagePosition(pageID);

                    stream = _dataStream;
                }

                stream.Position = pageInfo.Position;

                read = stream.Read(pageBuffer.Array, pageBuffer.Offset, pageBuffer.Count);

                ENSURE(read == PAGE_SIZE, "Page position {0} read only than {1} bytes (instead {2})", stream.Position, read, PAGE_SIZE);

                var page = new BasePage(pageBuffer);

                pageInfo.ColID = page.ColID;

                ENSURE(page.PageID == pageID, "Expect read pageID: {0} but header contains pageID: {1}", pageID, page.PageID);

                return page;
            }
            catch (Exception ex)
            {
                return new Result<BasePage>(null, ex);
            }
        }

        /// <summary>
        /// Handle any error avoiding throw exceptions during process. If exception must stop process (ioexceptions), throw exception
        /// Add errors to log and continue reading data file
        /// </summary>
        private void HandleError(Exception ex, PageInfo pageInfo)
        {
            var collection = _collections.FirstOrDefault(x => x.Value == pageInfo.ColID).Key;

            _errors.Add(new FileReaderError
            {
                Position = pageInfo.Position,
                Origin = pageInfo.Origin,
                PageID = pageInfo.PageID,
                PageType = pageInfo.PageType,
                Collection = collection,
                Message = ex.Message,
                Exception = ex,
            });

            if (ex is IOException)
            {
                // Código de erros HResult do IOException
                // https://learn.microsoft.com/pt-br/windows/win32/debug/system-error-codes--0-499-

                throw ex;
            }
        }

        private void HandleError(string message, PageInfo pageInfo) => this.HandleError(new Exception(message), pageInfo);

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _dataStream?.Dispose();
                _logStream?.Dispose();
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}