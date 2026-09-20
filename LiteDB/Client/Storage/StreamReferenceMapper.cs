using System;
using System.IO;

namespace LiteDB
{
    /// <summary>
    /// Converts model streams to references into the database's default file storage.
    /// </summary>
    internal sealed class StreamReferenceMapper
    {
        private const string ID_FIELD = "$id";
        private const string REF_FIELD = "$ref";
        private const string FILES_COLLECTION = "_files";

        private readonly Func<ILiteStorage<string>> _getStorage;

        public StreamReferenceMapper(Func<ILiteStorage<string>> getStorage)
        {
            _getStorage = getStorage ?? throw new ArgumentNullException(nameof(getStorage));
        }

        public BsonValue Serialize(Stream stream)
        {
            if (stream == null) return BsonValue.Null;

            var storage = _getStorage();
            string id;

            if (stream is LiteFileStream<string> fileStream &&
                storage is LiteStorage<string> liteStorage &&
                liteStorage.Owns(fileStream) &&
                storage.Exists(fileStream.FileInfo.Id))
            {
                id = fileStream.FileInfo.Id;
            }
            else
            {
                id = ObjectId.NewObjectId().ToString();
                storage.Upload(id, id, stream);
            }

            return new BsonDocument
            {
                [ID_FIELD] = id,
                [REF_FIELD] = FILES_COLLECTION
            };
        }

        public Stream Deserialize(BsonValue value)
        {
            if (value == null || value.IsDocument == false)
            {
                throw new LiteException(LiteException.MAPPING_ERROR,
                    $"Stream value must be a FileStorage reference, but was {value?.Type.ToString() ?? "null"}.");
            }

            var reference = value.AsDocument;

            if (reference[REF_FIELD] != FILES_COLLECTION || reference[ID_FIELD].IsString == false)
            {
                throw new LiteException(LiteException.MAPPING_ERROR,
                    "Stream value contains an invalid FileStorage reference.");
            }

            return _getStorage().OpenRead(reference[ID_FIELD].AsString);
        }
    }
}
