using LiteDB.Engine;
using System;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represents errors that occur during LiteDB operations.
    /// </summary>
    /// <remarks>
    /// <see cref="LiteException"/> is the primary exception type thrown by LiteDB for both user errors and internal failures.
    /// <para>The <see cref="ErrorCode"/> property provides a specific error code for programmatic error handling.</para>
    /// </remarks>
    public class LiteException : Exception
    {
        #region Errors code

        /// <summary>File not found error code.</summary>
        public const int FILE_NOT_FOUND = 101;
        
        /// <summary>Database is shutting down error code.</summary>
        public const int DATABASE_SHUTDOWN = 102;
        
        /// <summary>Invalid database format or password error code.</summary>
        public const int INVALID_DATABASE = 103;
        
        /// <summary>Database size limit exceeded error code.</summary>
        public const int FILE_SIZE_EXCEEDED = 105;
        
        /// <summary>Collection name size limit exceeded error code.</summary>
        public const int COLLECTION_LIMIT_EXCEEDED = 106;
        
        /// <summary>Attempt to drop primary key index error code.</summary>
        public const int INDEX_DROP_ID = 108;
        
        /// <summary>Duplicate key in unique index error code.</summary>
        public const int INDEX_DUPLICATE_KEY = 110;
        
        /// <summary>Invalid index key error code.</summary>
        public const int INVALID_INDEX_KEY = 111;
        
        /// <summary>Index not found error code.</summary>
        public const int INDEX_NOT_FOUND = 112;
        
        /// <summary>Invalid database reference format error code.</summary>
        public const int INVALID_DBREF = 113;
        
        /// <summary>Lock acquisition timeout error code.</summary>
        public const int LOCK_TIMEOUT = 120;
        
        /// <summary>Invalid shell command error code.</summary>
        public const int INVALID_COMMAND = 121;
        
        /// <summary>Collection name already exists error code.</summary>
        public const int ALREADY_EXISTS_COLLECTION_NAME = 122;
        
        /// <summary>Database file already open in another process error code.</summary>
        public const int ALREADY_OPEN_DATAFILE = 124;
        
        /// <summary>Invalid transaction state error code.</summary>
        public const int INVALID_TRANSACTION_STATE = 126;
        
        /// <summary>Index name size limit exceeded error code.</summary>
        public const int INDEX_NAME_LIMIT_EXCEEDED = 128;
        
        /// <summary>Invalid index name error code.</summary>
        public const int INVALID_INDEX_NAME = 129;
        
        /// <summary>Invalid collection name error code.</summary>
        public const int INVALID_COLLECTION_NAME = 130;
        
        /// <summary>Temporary engine already defined error code.</summary>
        public const int TEMP_ENGINE_ALREADY_DEFINED = 131;
        
        /// <summary>Invalid expression type error code.</summary>
        public const int INVALID_EXPRESSION_TYPE = 132;
        
        /// <summary>Collection not found error code.</summary>
        public const int COLLECTION_NOT_FOUND = 133;
        
        /// <summary>Collection already exists error code.</summary>
        public const int COLLECTION_ALREADY_EXIST = 134;
        
        /// <summary>Index already exists error code.</summary>
        public const int INDEX_ALREADY_EXIST = 135;
        
        /// <summary>Invalid field in UPDATE command error code.</summary>
        public const int INVALID_UPDATE_FIELD = 136;
        
        /// <summary>Engine instance already disposed error code.</summary>
        public const int ENGINE_DISPOSED = 137;

        /// <summary>Invalid format error code.</summary>
        public const int INVALID_FORMAT = 200;
        
        /// <summary>Document nesting depth exceeded error code.</summary>
        public const int DOCUMENT_MAX_DEPTH = 201;
        
        /// <summary>Invalid constructor for type instantiation error code.</summary>
        public const int INVALID_CTOR = 202;
        
        /// <summary>Unexpected token in expression parsing error code.</summary>
        public const int UNEXPECTED_TOKEN = 203;
        
        /// <summary>Invalid BSON data type error code.</summary>
        public const int INVALID_DATA_TYPE = 204;
        
        /// <summary>Property not mapped to BSON document error code.</summary>
        public const int PROPERTY_NOT_MAPPED = 206;
        
        /// <summary>Invalid type name for deserialization error code.</summary>
        public const int INVALID_TYPED_NAME = 207;
        
        /// <summary>Property requires public getter and setter error code.</summary>
        public const int PROPERTY_READ_WRITE = 209;
        
        /// <summary>Initial size not supported for encrypted databases error code.</summary>
        public const int INITIALSIZE_CRYPTO_NOT_SUPPORTED = 210;
        
        /// <summary>Invalid initial size value error code.</summary>
        public const int INVALID_INITIALSIZE = 211;
        
        /// <summary>Null character in string error code.</summary>
        public const int INVALID_NULL_CHAR_STRING = 212;
        
        /// <summary>Invalid free space on page error code.</summary>
        public const int INVALID_FREE_SPACE_PAGE = 213;
        
        /// <summary>Data type not assignable error code.</summary>
        public const int DATA_TYPE_NOT_ASSIGNABLE = 214;
        
        /// <summary>Avoid use of process error code.</summary>
        public const int AVOID_USE_OF_PROCESS = 215;
        
        /// <summary>File not encrypted error code.</summary>
        public const int NOT_ENCRYPTED = 216;
        
        /// <summary>Invalid password error code.</summary>
        public const int INVALID_PASSWORD = 217;
        
        /// <summary>Illegal deserialization type error code.</summary>
        public const int ILLEGAL_DESERIALIZATION_TYPE = 218;
        
        /// <summary>Entity initialization failed error code.</summary>
        public const int ENTITY_INITIALIZATION_FAILED = 219;
        
        /// <summary>Mapper not found error code.</summary>
        public const int MAPPER_NOT_FOUND = 220;
        
        /// <summary>Mapping error code.</summary>
        public const int MAPPING_ERROR = 221;
        
        /// <summary>Invalid datafile state error code.</summary>
        public const int INVALID_DATAFILE_STATE = 999;

        #endregion

        #region Ctor

        /// <summary>
        /// Gets the error code that identifies the type of error.
        /// </summary>
        public int ErrorCode { get; private set; }
        
        /// <summary>
        /// Gets the position in the input where the error occurred (primarily used for parsing errors).
        /// </summary>
        public long Position { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="LiteException"/> class with a specified error code and message.
        /// </summary>
        /// <param name="code">The error code that identifies the type of error.</param>
        /// <param name="message">The message that describes the error.</param>
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
        /// Gets a value indicating whether this error is critical and requires the engine to shut down and release all resources.
        /// </summary>
        /// <remarks>
        /// Critical errors (error code >= 900) indicate severe failures that compromise database integrity or engine state.
        /// When a critical error occurs, the engine should be stopped and all data files and memory should be released.
        /// </remarks>
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