using System;

namespace LiteDB
{
    /// <summary>
    /// Hand-written mapping for <see cref="LiteFileInfo{TFileId}"/>. File storage is LiteDB's own model, so it
    /// does not need runtime member discovery: this keeps file storage usable when trimmed and as Native AOT.
    /// Field names and order match what the reflection mapper produces from the attributes on LiteFileInfo.
    /// </summary>
    internal static class LiteFileInfoMapping<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(AotCompatibility.FileIdMembers)] TFileId>
    {
        /// <summary>
        /// Registers the mapping once per mapper. The entity mapper serves LINQ member resolution, the execution
        /// map converts documents.
        /// </summary>
        public static void EnsureRegistered(BsonMapper mapper)
        {
            mapper.TryRegisterGeneratedMapping(CreateEntityMapper(), CreateExecutionMap(mapper));
        }

        private static EntityMapper CreateEntityMapper()
        {
            var entity = new EntityMapper(typeof(LiteFileInfo<TFileId>))
            {
                CreateInstance = _ => new LiteFileInfo<TFileId>()
            };

            entity.Members.Add(Member("_id", nameof(LiteFileInfo<TFileId>.Id), typeof(TFileId), x => x.Id, (x, value) => x.Id = (TFileId)value, autoId: true));
            entity.Members.Add(Member("filename", nameof(LiteFileInfo<TFileId>.Filename), typeof(string), x => x.Filename, (x, value) => x.Filename = (string)value));
            entity.Members.Add(Member("mimeType", nameof(LiteFileInfo<TFileId>.MimeType), typeof(string), x => x.MimeType, (x, value) => x.MimeType = (string)value));
            entity.Members.Add(Member("length", nameof(LiteFileInfo<TFileId>.Length), typeof(long), x => x.Length, (x, value) => x.Length = (long)value));
            entity.Members.Add(Member("chunks", nameof(LiteFileInfo<TFileId>.Chunks), typeof(int), x => x.Chunks, (x, value) => x.Chunks = (int)value));
            entity.Members.Add(Member("uploadDate", nameof(LiteFileInfo<TFileId>.UploadDate), typeof(DateTime), x => x.UploadDate, (x, value) => x.UploadDate = (DateTime)value));
            entity.Members.Add(Member("metadata", nameof(LiteFileInfo<TFileId>.Metadata), typeof(BsonDocument), x => x.Metadata, (x, value) => x.Metadata = (BsonDocument)value));

            return entity;
        }

        private static MemberMapper Member(
            string fieldName,
            string memberName,
            Type dataType,
            Func<LiteFileInfo<TFileId>, object> getter,
            Action<LiteFileInfo<TFileId>, object> setter,
            bool autoId = false)
        {
            return new MemberMapper
            {
                AutoId = autoId,
                FieldName = fieldName,
                MemberName = memberName,
                DataType = dataType,
                UnderlyingType = dataType,
                IsEnumerable = false,
                Getter = entity => getter((LiteFileInfo<TFileId>)entity),
                Setter = (entity, value) => setter((LiteFileInfo<TFileId>)entity, value)
            };
        }

        private static GeneratedEntityMap<LiteFileInfo<TFileId>> CreateExecutionMap(BsonMapper mapper)
        {
            return new GeneratedEntityMap<LiteFileInfo<TFileId>>(
                (file, options) => Serialize(mapper, file, options),
                (document, options) => Deserialize(mapper, document))
            {
                // Every field name is fixed above, so naming conventions and member callbacks on the mapper do
                // not apply; a customized mapper must not make file storage unavailable.
                IsConfigurationIndependent = true
            };
        }

        private static BsonDocument Serialize(BsonMapper mapper, LiteFileInfo<TFileId> file, GeneratedExecutionOptions options)
        {
            var document = new BsonDocument { ["_id"] = mapper.SerializeFileId(file.Id) };

            Add(document, "filename", SerializeString(file.Filename, options), options);
            Add(document, "mimeType", SerializeString(file.MimeType, options), options);
            document["length"] = file.Length;
            document["chunks"] = file.Chunks;
            document["uploadDate"] = file.UploadDate;
            Add(document, "metadata", file.Metadata ?? BsonValue.Null, options);

            return document;
        }

        private static void Add(BsonDocument document, string field, BsonValue value, GeneratedExecutionOptions options)
        {
            if (value.IsNull && options.SerializeNullValues == false) return;

            document[field] = value;
        }

        private static BsonValue SerializeString(string value, GeneratedExecutionOptions options)
        {
            if (value == null) return BsonValue.Null;

            var text = options.TrimWhitespace ? value.Trim() : value;

            return options.EmptyStringToNull && text.Length == 0 ? BsonValue.Null : new BsonValue(text);
        }

        private static LiteFileInfo<TFileId> Deserialize(BsonMapper mapper, BsonDocument document)
        {
            var file = new LiteFileInfo<TFileId>();

            if (document.TryGetValue("_id", out var id)) file.Id = mapper.DeserializeFileId<TFileId>(id);
            if (document.TryGetValue("filename", out var filename)) file.Filename = filename.IsNull ? null : filename.AsString;
            if (document.TryGetValue("mimeType", out var mimeType)) file.MimeType = mimeType.IsNull ? null : mimeType.AsString;
            if (document.TryGetValue("length", out var length)) file.Length = length.AsInt64;
            if (document.TryGetValue("chunks", out var chunks)) file.Chunks = chunks.AsInt32;
            if (document.TryGetValue("uploadDate", out var uploadDate)) file.UploadDate = uploadDate.AsDateTime;
            if (document.TryGetValue("metadata", out var metadata)) file.Metadata = metadata.IsDocument ? metadata.AsDocument : null;

            return file;
        }
    }
}
