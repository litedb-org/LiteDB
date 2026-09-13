using System;
using System.Reflection;

namespace LiteDB
{
    /// <summary>
    /// The main exception for LiteDB
    /// </summary>
    public class LiteException : Exception
    {
        #region Errors code

        public const int FILE_NOT_FOUND = 101;
        public const int INVALID_DATABASE = 103;
        public const int INVALID_DATABASE_VERSION = 104;
        public const int FILE_SIZE_EXCEEDED = 105;
        public const int COLLECTION_LIMIT_EXCEEDED = 106;
        public const int INDEX_DROP_IP = 108;
        public const int INDEX_LIMIT_EXCEEDED = 109;
        public const int INDEX_DUPLICATE_KEY = 110;
        public const int INDEX_KEY_TOO_LONG = 111;
        public const int INDEX_NOT_FOUND = 112;
        public const int INVALID_DBREF = 113;
        public const int LOCK_TIMEOUT = 120;
        public const int INVALID_COMMAND = 121;
        public const int ALREADY_EXISTS_COLLECTION_NAME = 122;
        public const int DATABASE_WRONG_PASSWORD = 123;
        public const int READ_ONLY_DATABASE = 125;
        public const int TRANSACTION_NOT_SUPPORTED = 126;
        public const int SYNTAX_ERROR = 127;

        public const int INVALID_FORMAT = 200;
        public const int DOCUMENT_MAX_DEPTH = 201;
        public const int INVALID_CTOR = 202;
        public const int UNEXPECTED_TOKEN = 203;
        public const int INVALID_DATA_TYPE = 204;
        public const int PROPERTY_NOT_MAPPED = 206;
        public const int INVALID_TYPED_NAME = 207;
        public const int NEED_RECOVER = 208;
        public const int PROPERTY_READ_WRITE = 209;
        public const int INITIALSIZE_CRYPTO_NOT_SUPPORTED = 210;
        public const int INVALID_INITIALSIZE = 211;
        public const int INVALID_NULL_CHAR_STRING = 212;
        public const int INVALID_FREE_SPACE_PAGE = 213;
        public const int DATA_TYPE_NOT_ASSIGNABLE = 214;
        public const int AVOID_USE_OF_PROCESS = 215;

        #endregion

        #region Ctor

        public int ErrorCode { get; private set; }
        public string Line { get; private set; }
        public int Position { get; private set; }

        public LiteException(string message)
            : base(message)
        {
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

        #endregion

        #region Method Errors

        internal static LiteException FileNotFound(string fileId)
        {
            return new LiteException(FILE_NOT_FOUND, "File '{0}' not found.", fileId);
        }

        internal static LiteException InvalidDatabase()
        {
            return new LiteException(INVALID_DATABASE, "Datafile is not a LiteDB database.");
        }

        internal static LiteException InvalidDatabaseVersion(int version)
        {
            return new LiteException(INVALID_DATABASE_VERSION, "Invalid database version: {0}", version);
        }

        internal static LiteException FileSizeExceeded(long limit)
        {
            return new LiteException(FILE_SIZE_EXCEEDED, "Database size exceeds limit of {0}.", StorageUnitHelper.FormatFileSize(limit));
        }

        internal static LiteException CollectionLimitExceeded(int limit)
        {
            return new LiteException(COLLECTION_LIMIT_EXCEEDED, "This database exceeded the maximum limit of collection names size: {0} bytes", limit);
        }

        internal static LiteException IndexDropId()
        {
            return new LiteException(INDEX_DROP_IP, "Primary key index '_id' can't be dropped.");
        }

        internal static LiteException IndexLimitExceeded(string collection)
        {
            return new LiteException(INDEX_LIMIT_EXCEEDED, "Collection '{0}' exceeded the maximum limit of indices: {1}", collection, CollectionIndex.INDEX_PER_COLLECTION);
        }

        internal static LiteException IndexDuplicateKey(string field, BsonValue key)
        {
            return new LiteException(INDEX_DUPLICATE_KEY, "Cannot insert duplicate key in unique index '{0}'. The duplicate value is '{1}'.", field, key);
        }

        internal static LiteException IndexKeyTooLong()
        {
            return new LiteException(INDEX_KEY_TOO_LONG, "Index key must be less than {0} bytes.", IndexService.MAX_INDEX_LENGTH);
        }

        internal static LiteException IndexNotFound(string collection, string field)
        {
            return new LiteException(INDEX_NOT_FOUND, "Index not found on '{0}.{1}'.", collection, field);
        }

        internal static LiteException LockTimeout(TimeSpan ts)
        {
            return new LiteException(LOCK_TIMEOUT, "Timeout. Database is locked for more than {0}.", ts.ToString());
        }

        internal static LiteException InvalidCommand(string command)
        {
            return new LiteException(INVALID_COMMAND, "Command '{0}' is not a valid shell command.", command);
        }

        internal static LiteException AlreadyExistsCollectionName(string newName)
        {
            return new LiteException(ALREADY_EXISTS_COLLECTION_NAME, "New collection name '{0}' already exists.", newName);
        }

        internal static LiteException DatabaseWrongPassword()
        {
            return new LiteException(DATABASE_WRONG_PASSWORD, "Invalid database password.");
        }

        internal static LiteException ReadOnlyDatabase()
        {
            return new LiteException(READ_ONLY_DATABASE, "This action are not supported because database was opened in read only mode.");
        }

        internal static LiteException InvalidDbRef(string path)
        {
            return new LiteException(INVALID_DBREF, "Invalid value for DbRef in path \"{0}\". Value must be document like {{ $ref: \"?\", $id: ? }}", path);
        }

        internal static LiteException TransactionNotSupported(string method)
        {
            return new LiteException(TRANSACTION_NOT_SUPPORTED, "Transactions are not supported here: " + method);
        }

        internal static LiteException NeedRecover()
        {
            return new LiteException(NEED_RECOVER, "Your datafile did not terminate properly during the writing process. Reopen the file");
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

        internal static LiteException UnexpectedToken(string token)
        {
            return new LiteException(UNEXPECTED_TOKEN, "Unexpected JSON token: {0}", token);
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

        internal static LiteException SyntaxError(StringScanner s, string message = "Unexpected token")
        {
            return new LiteException(SYNTAX_ERROR, message)
            {
                Line = s.Source,
                Position = s.Index
            };
        }

        internal static LiteException InvalidNullCharInString()
        {
            return new LiteException(INVALID_NULL_CHAR_STRING, "Invalid null character (\\0) was found in the string");
        }

        internal static LiteException InvalidFreeSpacePage(uint pageID, int freeBytes, int length)
        {
            return new LiteException(INVALID_FREE_SPACE_PAGE, $"An operation that would corrupt page {pageID} was prevented. The operation required {length} free bytes, but the page had only {freeBytes} available.");
        }

        internal static LiteException DataTypeNotAssignable(string type1, string type2)
        {
            return new LiteException(DATA_TYPE_NOT_ASSIGNABLE, $"Data type {type1} is not assignable from data type {type2}");
        }

        internal static LiteException AvoidUseOfProcess()
        {
            return new LiteException(AVOID_USE_OF_PROCESS, $"LiteDB do not accept System.Diagnostics.Process class in deserialize mapper");
        }

        #endregion
    }
}