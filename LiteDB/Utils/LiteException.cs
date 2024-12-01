using LiteDB.Engine;
using System;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// The main exception for LiteDB
    /// </summary>
    public class LiteException : Exception
    {
        #region Errors code

        public const int FILE_NOT_FOUND = 101;
        public const int DATABASE_SHUTDOWN = 102;
        public const int INVALID_DATABASE = 103;
        public const int FILE_SIZE_EXCEEDED = 105;
        public const int COLLECTION_LIMIT_EXCEEDED = 106;
        public const int INDEX_DROP_ID = 108;
        public const int INDEX_DUPLICATE_KEY = 110;
        public const int INVALID_INDEX_KEY = 111;
        public const int INDEX_NOT_FOUND = 112;
        public const int INVALID_DBREF = 113;
        public const int LOCK_TIMEOUT = 120;
        public const int INVALID_COMMAND = 121;
        public const int ALREADY_EXISTS_COLLECTION_NAME = 122;
        public const int ALREADY_OPEN_DATAFILE = 124;
        public const int INVALID_TRANSACTION_STATE = 126;
        public const int INDEX_NAME_LIMIT_EXCEEDED = 128;
        public const int INVALID_INDEX_NAME = 129;
        public const int INVALID_COLLECTION_NAME = 130;
        public const int TEMP_ENGINE_ALREADY_DEFINED = 131;
        public const int INVALID_EXPRESSION_TYPE = 132;
        public const int COLLECTION_NOT_FOUND = 133;
        public const int COLLECTION_ALREADY_EXIST = 134;
        public const int INDEX_ALREADY_EXIST = 135;
        public const int INVALID_UPDATE_FIELD = 136;
        public const int ENGINE_DISPOSED = 137;

        public const int INVALID_FORMAT = 200;
        public const int DOCUMENT_MAX_DEPTH = 201;
        public const int INVALID_CTOR = 202;
        public const int UNEXPECTED_TOKEN = 203;
        public const int INVALID_DATA_TYPE = 204;
        public const int PROPERTY_NOT_MAPPED = 206;
        public const int INVALID_TYPED_NAME = 207;
        public const int PROPERTY_READ_WRITE = 209;
        public const int INITIALSIZE_CRYPTO_NOT_SUPPORTED = 210;
        public const int INVALID_INITIALSIZE = 211;
        public const int INVALID_NULL_CHAR_STRING = 212;
        public const int INVALID_FREE_SPACE_PAGE = 213;
        public const int DATA_TYPE_NOT_ASSIGNABLE = 214;
        public const int AVOID_USE_OF_PROCESS = 215;
        public const int NOT_ENCRYPTED = 216;
        public const int INVALID_PASSWORD = 217;
        public const int ILLEGAL_DESERIALIZATION_TYPE = 218;
        public const int ENTITY_INITIALIZATION_FAILED = 219;
        public const int MAPPER_NOT_FOUND = 220;
        public const int MAPPING_ERROR = 221;
        

        public const int INVALID_DATAFILE_STATE = 999;

        #endregion

        #region Ctor

        public int ErrorCode { get; private set; }
        public long Position { get; private set; }

        public LiteException(int code, string message)
            : base(message)
        {
            this.ErrorCode = code;
        }

        internal LiteException(int code, string message, params object[] args)
            : base(string.Format(message, args))
        {
            this.ErrorCode = code;
        }

        internal LiteException (int code, Exception inner, string message, params object[] args)
        : base (string.Format (message, args), inner)
        {
            this.ErrorCode = code;
        }

        /// <summary>
        /// Critical error should be stop engine and release data files and all memory allocation
        /// </summary>
        public bool IsCritical => this.ErrorCode >= 900;

        #endregion

        #region Method Errors

        internal static LiteException FileNotFound(object fileId)
        {
            return new LiteException(FILE_NOT_FOUND, "File '{0}' not found.", fileId);
        }

        internal static LiteException DatabaseShutdown()
        {
            return new LiteException(DATABASE_SHUTDOWN, "Database is in shutdown process.");
        }

        internal static LiteException InvalidDatabase()
        {
            return new LiteException(INVALID_DATABASE, "File is not a valid LiteDB database format or contains a invalid password.");
        }

        internal static LiteException FileSizeExceeded(long limit)
        {
            return new LiteException(FILE_SIZE_EXCEEDED, "Database size exceeds limit of {0}.", FileHelper.FormatFileSize(limit));
        }

        internal static LiteException CollectionLimitExceeded(int limit)
        {
            return new LiteException(COLLECTION_LIMIT_EXCEEDED, "This database exceeded the maximum limit of collection names size: {0} bytes", limit);
        }

        internal static LiteException IndexNameLimitExceeded(int limit)
        {
            return new LiteException(INDEX_NAME_LIMIT_EXCEEDED, "This collection exceeded the maximum limit of indexes names/expression size: {0} bytes", limit);
        }

        internal static LiteException InvalidIndexName(string name, string collection, string reason)
        {
            return new LiteException(INVALID_INDEX_NAME, "Invalid index name '{0}' on collection '{1}': {2}", name, collection, reason);
        }

        internal static LiteException InvalidCollectionName(string name, string reason)
        {
            return new LiteException(INVALID_COLLECTION_NAME, "Invalid collection name '{0}': {1}", name, reason);
        }

        internal static LiteException IndexDropId()
        {
            return new LiteException(INDEX_DROP_ID, "Primary key index '_id' can't be dropped.");
        }

        internal static LiteException TempEngineAlreadyDefined()
        {
            return new LiteException(TEMP_ENGINE_ALREADY_DEFINED, "Temporary engine already defined or auto created.");
        }

        internal static LiteException CollectionNotFound(string key)
        {
            return new LiteException(COLLECTION_NOT_FOUND, "Collection not found: '{0}'", key);
        }

        internal static LiteException InvalidExpressionType(BsonExpression expr, BsonExpressionType type)
        {
            return new LiteException(INVALID_EXPRESSION_TYPE, "Expression '{0}' must be a {1} type.", expr.Source, type);
        }

        internal static LiteException InvalidExpressionTypePredicate(BsonExpression expr)
        {
            return new LiteException(INVALID_EXPRESSION_TYPE, "Expression '{0}' are not supported as predicate expression.", expr.Source);
        }

        internal static LiteException CollectionAlreadyExist(string key)
        {
            return new LiteException(COLLECTION_ALREADY_EXIST, "Collection already exist: '{0}'", key);
        }

        internal static LiteException IndexAlreadyExist(string name)
        {
            return new LiteException(INDEX_ALREADY_EXIST, "Index name '{0}' already exist with a differnt expression. Try drop index first.", name);
        }

        internal static LiteException InvalidUpdateField(string field)
        {
            return new LiteException(INVALID_UPDATE_FIELD, "'{0}' can't be modified in UPDATE command.", field);
        }

        internal static LiteException IndexDuplicateKey(string field, BsonValue key)
        {
            return new LiteException(INDEX_DUPLICATE_KEY, "Cannot insert duplicate key in unique index '{0}'. The duplicate value is '{1}'.", field, key);
        }

        internal static LiteException InvalidIndexKey(string text)
        {
            return new LiteException(INVALID_INDEX_KEY, text);
        }

        internal static LiteException IndexNotFound(string name)
        {
            return new LiteException(INDEX_NOT_FOUND, "Index not found '{0}'.", name);
        }

        internal static LiteException LockTimeout(string mode, TimeSpan ts)
        {
            return new LiteException(LOCK_TIMEOUT, "Database lock timeout when entering in {0} mode after {1}", mode, ts.ToString());
        }

        internal static LiteException LockTimeout(string mode, string collection, TimeSpan ts)
        {
            return new LiteException(LOCK_TIMEOUT, "Collection '{0}' lock timeout when entering in {1} mode after {2}", collection, mode, ts.ToString());
        }

        internal static LiteException InvalidCommand(string command)
        {
            return new LiteException(INVALID_COMMAND, "Command '{0}' is not a valid shell command.", command);
        }

        internal static LiteException AlreadyExistsCollectionName(string newName)
        {
            return new LiteException(ALREADY_EXISTS_COLLECTION_NAME, "New collection name '{0}' already exists.", newName);
        }

        internal static LiteException AlreadyOpenDatafile(string filename)
        {
            return new LiteException(ALREADY_OPEN_DATAFILE, "Your datafile '{0}' is open in another process.", filename);
        }

        internal static LiteException InvalidDbRef(string path)
        {
            return new LiteException(INVALID_DBREF, "Invalid value for DbRef in path '{0}'. Value must be document like {{ $ref: \"?\", $id: ? }}", path);
        }

        internal static LiteException AlreadyExistsTransaction()
        {
            return new LiteException(INVALID_TRANSACTION_STATE, "The current thread already contains an open transaction. Use the Commit/Rollback method to release the previous transaction.");
        }

        internal static LiteException CollectionLockerNotFound(string collection)
        {
            return new LiteException(INVALID_TRANSACTION_STATE, "Collection locker '{0}' was not found inside dictionary.", collection);
        }

        internal static LiteException InvalidFormat(string field)
        {
            return new LiteException(INVALID_FORMAT, "Invalid format: {0}", field);
        }

        internal static LiteException DocumentMaxDepth(int depth, Type type)
        {
            return new LiteException(DOCUMENT_MAX_DEPTH, "Document has more than {0} nested documents in '{1}'. Check for circular references (use DbRef).", depth, type == null ? "-" : type.Name);
        }

        internal static LiteException InvalidCtor(Type type, Exception inner)
        {
            return new LiteException(INVALID_CTOR, inner, "Failed to create instance for type '{0}' from assembly '{1}'. Checks if the class has a public constructor with no parameters.", type.FullName, type.AssemblyQualifiedName);
        }

        internal static LiteException UnexpectedToken(Token token, string expected = null)
        {
            var position = (token?.Position - (token?.Value?.Length ?? 0)) ?? 0;
            var str = token?.Type == TokenType.EOF ? "[EOF]" : token?.Value ?? "";
            var exp = expected == null ? "" : $" Expected `{expected}`.";

            return new LiteException(UNEXPECTED_TOKEN, $"Unexpected token `{str}` in position {position}.{exp}")
            {
                Position = position
            };
        }

        internal static LiteException UnexpectedToken(string message, Token token)
        {
            var position = (token?.Position - (token?.Value?.Length ?? 0)) ?? 0;

            return new LiteException(UNEXPECTED_TOKEN, message)
            {
                Position = position
            };
        }

        internal static LiteException InvalidDataType(string field, BsonValue value)
        {
            return new LiteException(INVALID_DATA_TYPE, "Invalid BSON data type '{0}' on field '{1}'.", value.Type, field);
        }

        internal static LiteException PropertyReadWrite(PropertyInfo prop)
        {
            return new LiteException(PROPERTY_READ_WRITE, "'{0}' property must have public getter and setter.", prop.Name);
        }

        internal static LiteException PropertyNotMapped(string name)
        {
            return new LiteException(PROPERTY_NOT_MAPPED, "Property '{0}' was not mapped into BsonDocument.", name);
        }

        internal static LiteException InvalidTypedName(string type)
        {
            return new LiteException(INVALID_TYPED_NAME, "Type '{0}' not found in current domain (_type format is 'Type.FullName, AssemblyName').", type);
        }

        internal static LiteException InitialSizeCryptoNotSupported()
        {
            return new LiteException(INITIALSIZE_CRYPTO_NOT_SUPPORTED, "Initial Size option is not supported for encrypted datafiles.");
        }

        internal static LiteException InvalidInitialSize()
        {
            return new LiteException(INVALID_INITIALSIZE, "Initial Size must be a multiple of page size ({0} bytes).", PAGE_SIZE);
        }

        internal static LiteException EngineDisposed()
        {
            return new LiteException(ENGINE_DISPOSED, "This engine instance already disposed.");
        }

        internal static LiteException InvalidNullCharInString()
        {
            return new LiteException(INVALID_NULL_CHAR_STRING, "Invalid null character (\\0) was found in the string");
        }

        internal static LiteException InvalidPageType(PageType pageType, BasePage page)
        {
            var sb = new StringBuilder($"Invalid {pageType} on {page.PageID}. ");

            sb.Append($"Full zero: {page.Buffer.All(0)}. ");
            sb.Append($"Page Type: {page.PageType}. ");
            sb.Append($"Prev/Next: {page.PrevPageID}/{page.NextPageID}. ");
            sb.Append($"UniqueID: {page.Buffer.UniqueID}. ");
            sb.Append($"ShareCounter: {page.Buffer.ShareCounter}. ");

            return new LiteException(0, sb.ToString());
        }

        internal static LiteException InvalidFreeSpacePage(uint pageID, int freeBytes, int length)
        {
            return new LiteException(INVALID_FREE_SPACE_PAGE, $"An operation that would corrupt page {pageID} was prevented. The operation required {length} free bytes, but the page had only {freeBytes} available.");
        }

        internal static LiteException DataTypeNotAssignable(string type1, string type2)
        {
            return new LiteException(DATA_TYPE_NOT_ASSIGNABLE, $"Data type {type1} is not assignable from data type {type2}");
        }
            
        internal static LiteException FileNotEncrypted()
        {
            return new LiteException(NOT_ENCRYPTED, "File is not encrypted.");
        }

        internal static LiteException InvalidPassword()
        {
            return new LiteException(INVALID_PASSWORD, "Invalid password.");
        }

        internal static LiteException IllegalDeserializationType(string typeName)
        {
            return new LiteException(ILLEGAL_DESERIALIZATION_TYPE, $"Illegal deserialization type: {typeName}");
        }

        internal static LiteException InvalidDatafileState(string message)
        {
            return new LiteException(INVALID_DATAFILE_STATE, message);
        }

        #endregion
    }
}