using System;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// A file-backed database in its own temporary directory, opened through the public
    /// <see cref="LiteDatabase"/> API in Direct or Shared mode, with the property's collections
    /// created up front (so no case pays for collection creation) and the TIMEOUT pragma set.
    /// Disposal deletes the directory, including WAL and Shared coordination files.
    /// </summary>
    public sealed class PropertyDatabase : IDisposable
    {
        public const int MaxCollections = 2;

        /// <summary>Key outside the model's key space, used only by the quiescence probe.</summary>
        public const int ProbeKey = 0;

        private readonly string _directory;

        public PropertyDatabase(PropertyOptions options)
        {
            _directory = Path.Combine(Path.GetTempPath(), "litedb-pbt-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_directory);
            this.Filename = Path.Combine(_directory, "property.db");
            try
            {
                var connection = new ConnectionString
                {
                    Filename = this.Filename,
                    Connection = options.Mode,
                    DurableCommits = options.DurableCommits,
                };
                var engine = connection.CreateEngine();
                if (options.EngineDecorator != null) engine = options.EngineDecorator(engine);
                this.Database = new LiteDatabase(engine);
                this.Database.Pragma(Pragmas.TIMEOUT, options.LockTimeoutSeconds);
                for (var c = 0; c < MaxCollections; c++)
                {
                    var collection = this.Database.GetCollection(CollectionName(c));
                    collection.Insert(new BsonDocument { ["_id"] = ProbeKey, ["p"] = 0 });
                    collection.Delete(ProbeKey);
                }
            }
            catch
            {
                this.Dispose();
                throw;
            }
        }

        public string Filename { get; }

        public LiteDatabase Database { get; private set; }

        public static string CollectionName(int index) => "pbt" + index;

        /// <summary>Committed contents of a collection, read on the calling thread, in canonical form.</summary>
        public string ReadContents(int collection) =>
            DataOperations.Canonical(this.Database.GetCollection(CollectionName(collection)).FindAll()
                .Where(d => d["_id"].AsInt32 != ProbeKey));

        /// <summary>
        /// Write and remove the probe key in each collection (a lock or mutex still retained by an
        /// idle thread makes this time out or block), then read the committed contents.
        /// </summary>
        public string[] ProbeAndRead(int collections)
        {
            var contents = new string[collections];
            for (var c = 0; c < collections; c++)
            {
                var collection = this.Database.GetCollection(CollectionName(c));
                collection.Upsert(new BsonDocument { ["_id"] = ProbeKey, ["p"] = 0 });
                collection.Delete(ProbeKey);
                contents[c] = this.ReadContents(c);
            }
            return contents;
        }

        public void Dispose()
        {
            try
            {
                this.Database?.Dispose();
            }
            finally
            {
                this.Database = null;
                DeleteDirectory(_directory);
            }
        }

        private static void DeleteDirectory(string directory)
        {
            // Windows may keep a handle briefly after disposal (antivirus, delayed close).
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
