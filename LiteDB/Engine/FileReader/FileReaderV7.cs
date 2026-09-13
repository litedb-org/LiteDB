using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Internal class to read old LiteDB v4 database version (datafile v7 structure)
    /// </summary>
    internal class FileReaderV7 : IFileReader
    {
        // v7 uses 4k page size
        private const int V7_PAGE_SIZE = 4096;

        private readonly EngineSettings _settings;
        private Stream _stream;
        private AesEncryption _aes;
        private BsonDocument _header;

        private byte[] _buffer = new byte[V7_PAGE_SIZE];
        private bool _disposedValue;
        private static readonly byte[] arrayByteEmpty = new byte[0];

        public IDictionary<string, BsonValue> GetPragmas() => new Dictionary<string, BsonValue>()
        {
            { Pragmas.USER_VERSION, _header["userVersion"].AsInt32 },
            { Pragmas.CHECKPOINT, 1000 },
            { Pragmas.TIMEOUT, 60 },
            { Pragmas.LIMIT_SIZE, long.MaxValue },
            { Pragmas.UTC_DATE, true },
        };

        public FileReaderV7(EngineSettings settings)
        {
            _settings = settings;
        }

        public void Open()
        {
            var streamFactory = _settings.CreateDataFactory(false);

            // open datafile from stream factory
            _stream = streamFactory.GetStream(true, true);

            // only userVersion was avaiable in old file format versions
            _header = this.ReadPage(0);

            if (_settings.Password == null && _header["salt"].AsBinary.IsFullZero() == false)
            {
                throw LiteException.InvalidPassword();
            }
            else if (_settings.Password != null)
            {
                if (_header["salt"].AsBinary.IsFullZero())
                {
                    throw LiteException.FileNotEncrypted();
                }

                var hash = AesEncryption.HashSHA1(_settings.Password);

                if (hash.SequenceEqual(_header["password"].AsBinary) == false)
                {
                    throw LiteException.InvalidPassword();
                }
            }

            _aes = _settings.Password == null ?
                null :
                new AesEncryption(_settings.Password, _header["salt"].AsBinary);
        }

        /// <summary>
        /// Check header slots to test if data file is a LiteDB FILE_VERSION = v7
        /// </summary>
        public static bool IsVersion(byte[] buffer)
        {
            var header = Encoding.UTF8.GetString(buffer, 25, HeaderPage.HEADER_INFO.Length);
            var version = buffer[52];

            return (header == HeaderPage.HEADER_INFO && version == 7);
        }

        /// <summary>
        /// Read all collection based on header page
        /// </summary>
        public IEnumerable<string> GetCollections()
        {
            return _header["collections"].AsDocument.Keys;
        }

        /// <summary>
        /// Read all indexes from all collection pages
        /// </summary>
        public IEnumerable<IndexInfo> GetIndexes(string collection)
        {
            var pageID = (uint)_header["collections"].AsDocument[collection].AsInt32;
            var page = this.ReadPage(pageID);

            foreach(var index in page["indexes"].AsArray)
            {
                string name = Regex.Replace(index["name"].AsString, @"[^a-z0-9]", "", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                if(name.Length > INDEX_NAME_MAX_LENGTH)
                {
                    name = name.Substring(0, INDEX_NAME_MAX_LENGTH);
                }

                yield return new IndexInfo
                {
                    Collection = collection,
                    Name = name,
                    Expression = index["expression"].AsString,
                    Unique = index["unique"].AsBoolean
                };
            }
        }

        /// <summary>
        /// Get all document using an indexInfo as start point (_id index).
        /// </summary>
        public IEnumerable<BsonDocument> GetDocuments(string collection)
        {
            var colPageID = (uint)_header["collections"].AsDocument[collection].AsInt32;
            var col = this.ReadPage(colPageID);
            var headPageID = (uint)col["indexes"][0]["headPageID"].AsInt32;

            var indexPages = this.VisitIndexPages(headPageID);

            foreach(var indexPageID in indexPages)
            {
                var indexPage = this.ReadPage(indexPageID);

                foreach(var node in indexPage["nodes"].AsArray)
                {
                    var dataBlock = node["dataBlock"];

                    // if datablock link to a data page
                    if (dataBlock["pageID"].AsInt32 != -1)
                    {
                        // read dataPage and data block
                        var dataPage = this.ReadPage((uint)dataBlock["pageID"].AsInt32);

                        if (dataPage["pageType"].AsInt32 != 4) continue;

                        var block = dataPage["blocks"].AsArray.FirstOrDefault(x => x["index"] == dataBlock["index"])?.AsDocument;

                        if (block == null) continue;

                        // read byte[] from block or from extend pages
                        var data = block["extendPageID"] == -1 ?
                            block["data"].AsBinary :
                            this.ReadExtendData((uint)block["extendPageID"].AsInt32);

                        if (data.Length == 0) continue;

                        // BSON format still same from all version
                        var doc = BsonSerializer.Deserialize(data);

                        // change _id PK in _chunks collection
                        if (collection == "_chunks")
                        {
                            var parts = doc["_id"].AsString.Split('\\');

                            if (!int.TryParse(parts[1], out var n)) throw LiteException.InvalidFormat("_id");

                            doc["_id"] = new BsonDocument
                            {
                                ["f"] = parts[0],
                                ["n"] = n
                            };
                        }

                        yield return doc;
                    }
                }
            }
        }

        /// <summary>
        /// Read all database pages from v7 structure into a flexible BsonDocument - only read what really needs
        /// </summary>
        private BsonDocument ReadPage(uint pageID)
        {
            if (pageID * V7_PAGE_SIZE > _stream.Length) return null;

            _stream.Position = pageID * V7_PAGE_SIZE; // v7 uses 4k page size

            _stream.Read(_buffer, 0, V7_PAGE_SIZE);

            // decrypt encrypted page (except header page - header are plain data)
            if (_aes != null && pageID > 0)
            {
                _buffer = _aes.Decrypt(_buffer);
            }

            var reader = new ByteReader(_buffer);

            // reading page header
            var page = new BsonDocument
            {
                ["pageID"] = (int)reader.ReadUInt32(),
                ["pageType"] = (int)reader.ReadByte(),
                ["prevPageID"] = (int)reader.ReadUInt32(),
                ["nextPageID"] = (int)reader.ReadUInt32(),
                ["itemCount"] = (int)reader.ReadUInt16()
            };

            // skip freeByte + reserved
            reader.ReadBytes(2 + 8);

            #region Header (1)

            // read header
            if (page["pageType"] == 1)
            {
                var info = reader.ReadString(27);
                var ver = reader.ReadByte();

                if (string.CompareOrdinal(info, HeaderPage.HEADER_INFO) != 0 || ver != 7)
                {
                    throw LiteException.InvalidDatabase();
                }

                // skip ChangeID + FreeEmptyPageID + LastPageID
                reader.ReadBytes(2 + 4 + 4);
                page["userVersion"] = (int)reader.ReadUInt16();
                page["password"] = reader.ReadBytes(20);
                page["salt"] = reader.ReadBytes(16);
                page["collections"] = new BsonDocument();

                var cols = reader.ReadByte();

                for (var i = 0; i < cols; i++)
                {
                    var name = reader.ReadString();
                    var colPageID = reader.ReadUInt32();

                    page["collections"][name] = (int)colPageID;
                }
            }

            #endregion

            #region Collection (2)

            // collection page
            else if (page["pageType"] == 2)
            {
                page["collectionName"] = reader.ReadString();
                page["indexes"] = new BsonArray();
                reader.ReadBytes(12);

                for(var i = 0; i < 16; i++)
                {
                    var index = new BsonDocument();

                    var field = reader.ReadString();
                    var eq = field.IndexOf('=');

                    if (eq > 0)
                    {
                        index["name"] = field.Substring(0, eq);
                        index["expression"] = field.Substring(eq + 1);
                    }
                    else
                    {
                        index["name"] = field;
                        index["expression"] = "$." + field;
                    }

                    index["unique"] = reader.ReadBoolean();
                    index["headPageID"] = (int)reader.ReadUInt32();

                    // skip HeadNode (index) + TailNode + FreeIndexPageID
                    reader.ReadBytes(2 + 6 + 4);

                    if (field.Length > 0)
                    {
                        page["indexes"].AsArray.Add(index);
                    }
                }
            }

            #endregion

            #region Index (3)

            else if (page["pageType"] == 3)
            {
                page["nodes"] = new BsonArray();

                for(var i = 0; i < page["itemCount"].AsInt32; i++)
                {
                    var node = new BsonDocument
                    {
                        ["index"] = (int)reader.ReadUInt16()
                    };

                    var levels = reader.ReadByte();

                    // skip Slot + PrevNode + NextNode
                    reader.ReadBytes(1 + 6 + 6);

                    var length = reader.ReadUInt16();

                    // skip DataType + KeyValue
                    reader.ReadBytes(1 + length);

                    node["dataBlock"] = new BsonDocument
                    {
                        ["pageID"] = (int)reader.ReadUInt32(),
                        ["index"] = (int)reader.ReadUInt16()
                    };

                    // reading Prev[0]
                    node["prev"] = new BsonDocument
                    {
                        ["pageID"] = (int)reader.ReadUInt32(),
                        ["index"] = (int)reader.ReadUInt16()
                    };

                    // reading Next[0]
                    node["next"] = new BsonDocument
                    {
                        ["pageID"] = (int)reader.ReadUInt32(),
                        ["index"] = (int)reader.ReadUInt16()
                    };

                    // skip Prev/Next[1..N]
                    reader.ReadBytes((levels - 1) * (6 + 6));

                    page["nodes"].AsArray.Add(node);
                }
            }

            #endregion

            #region Data (4)

            else if (page["pageType"] == 4)
            {
                page["blocks"] = new BsonArray();

                for (var i = 0; i < page["itemCount"].AsInt32; i++)
                {
                    var block = new BsonDocument
                    {
                        ["index"] = (int)reader.ReadUInt16(),
                        ["extendPageID"] = (int)reader.ReadUInt32()
                    };

                    var length = reader.ReadUInt16();

                    block["data"] = reader.ReadBytes(length);

                    page["blocks"].AsArray.Add(block);
                }
            }

            #endregion

            #region Extend (5)

            else if (page["pageType"] == 5)
            {
                page["data"] = reader.ReadBytes(page["itemCount"].AsInt32);
            }

            #endregion

            return page;
        }

        /// <summary>
        /// Read extend data block
        /// </summary>
        private byte[] ReadExtendData(uint extendPageID)
        {
            // read all extended pages and build byte array
            using (var buffer = new MemoryStream())
            {
                while(extendPageID != uint.MaxValue)
                {
                    var page = this.ReadPage(extendPageID);

                    if (page["pageType"].AsInt32 != 5) return arrayByteEmpty;

                    buffer.Write(page["data"].AsBinary, 0, page["itemCount"].AsInt32);

                    extendPageID = (uint)page["nextPageID"].AsInt32;
                }

                return buffer.ToArray();
            }
        }

        /// <summary>
        /// Visit all index pages by starting index page. Get a list with all index pages from a collection
        /// </summary>
        private HashSet<uint> VisitIndexPages(uint startPageID)
        {
            var toVisit = new HashSet<uint>
            {
                startPageID
            };

            var visited = new HashSet<uint>();

            while (toVisit.Count > 0)
            {
                var indexPageID = toVisit.First();

                toVisit.Remove(indexPageID);

                var indexPage = this.ReadPage(indexPageID);

                if (indexPage == null || indexPage["pageType"] != 3) continue;

                visited.Add(indexPageID);

                foreach(var node in indexPage["nodes"].AsArray)
                {
                    var prev = (uint)node["prev"]["pageID"].AsInt32;
                    var next = (uint)node["next"]["pageID"].AsInt32;

                    if (!visited.Contains(prev)) toVisit.Add(prev);
                    if (!visited.Contains(next)) toVisit.Add(next);
                }
            }

            return visited;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _stream?.Dispose();
                    _aes?.Dispose();
                }

                _disposedValue = true; // no external resources
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}