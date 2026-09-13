using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace LiteDB
{
    public partial class LiteStorage<TFileId>
    {
        /// <summary>
        /// Open/create a file and return a stream that atomically appends to its content.
        /// Existing file metadata is preserved. A failed append is rolled back when this
        /// method owns the current transaction. The stream must be used and disposed on
        /// the opening thread. A caller-owned transaction remains the caller's responsibility.
        /// </summary>
        /// <param name="id">The identifier of the file to append.</param>
        /// <param name="filename">The filename used only when a new file is created.</param>
        /// <param name="metadata">The metadata used only when a new file is created.</param>
        /// <returns>A write-only stream positioned at the end of the file.</returns>
        public LiteFileStream<TFileId> OpenAppend(TFileId id, string filename, BsonDocument metadata = null)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (filename.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(filename));

            var sync = LiteStorageAppendLock.Get(_db);
            var ownsTransaction = false;

            Monitor.Enter(sync);

            try
            {
                ownsTransaction = _db.BeginTrans();

                var fileId = _db.Mapper.Serialize(typeof(TFileId), id);
                var file = _files.Query()
                    .Where("_id = @0", fileId)
                    .ForUpdate()
                    .FirstOrDefault();

                if (file == null)
                {
                    file = new LiteFileInfo<TFileId>
                    {
                        Id = id,
                        Filename = System.IO.Path.GetFileName(filename),
                        MimeType = MimeTypeConverter.GetMimeType(filename),
                        Metadata = metadata ?? new BsonDocument()
                    };

                    // Materialize the file collection before the chunks collection is locked.
                    _files.Upsert(file);
                }

                file.SetReference(fileId, _files, _chunks);

                return file.OpenAppend(success =>
                {
                    try
                    {
                        if (ownsTransaction)
                        {
                            if (success) _db.Commit();
                            else _db.Rollback();
                        }
                    }
                    finally
                    {
                        Monitor.Exit(sync);
                    }
                });
            }
            catch
            {
                try
                {
                    if (ownsTransaction) _db.Rollback();
                }
                finally
                {
                    Monitor.Exit(sync);
                }

                throw;
            }
        }
    }

    internal static class LiteStorageAppendLock
    {
        private static readonly ConditionalWeakTable<ILiteDatabase, object> Locks =
            new ConditionalWeakTable<ILiteDatabase, object>();

        public static object Get(ILiteDatabase database)
        {
            return Locks.GetValue(database, _ => new object());
        }
    }
}
