using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Engine;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// Prior-state builders shared by the teardown drivers. A checkpointed template file holds the
    /// committed collections (built once per shape and copied per case); each case then adds recent
    /// writes that stay in the WAL, and the participants its prior state asks for.
    /// <list type="bullet">
    /// <item><c>rows</c>: <see cref="TeardownPrior.Documents"/> small documents, acknowledged in the ledger.</item>
    /// <item><c>many</c>: 150 documents, enough that a Shared read streams under a reader lease.</item>
    /// <item><c>big</c> (spilled sort): 1100 documents whose sort keys exceed one 800 KiB sort container.</item>
    /// </list>
    /// </summary>
    internal static class TeardownStates
    {
        public const int Recent = 100;
        public const int Pending = 200;
        private const int ManyCount = 150;
        private const int BigCount = 1100;
        private static readonly string TemplateRoot = Path.Combine(Path.GetTempPath(), "litedb-teardown-templates", Guid.NewGuid().ToString("N"));
        private static readonly ConcurrentDictionary<string, Lazy<string>> Templates = new ConcurrentDictionary<string, Lazy<string>>();

        /// <summary>Copy the template for <paramref name="c"/>'s prior state to a fresh database file and ledger its contents.</summary>
        public static string Database(TeardownCase c, string name = "sweep.db")
        {
            var prior = c.Prior;
            var key = $"{prior.Documents}-{prior.SpilledSort}-{prior.Upload}-{prior.Encrypted}";
            var template = Templates.GetOrAdd(key, _ => new Lazy<string>(() => Build(key, prior, c.Password))).Value;
            var path = c.File(name);
            File.Copy(template, path);
            for (var id = 1; id <= prior.Documents; id++) c.Ledger.Acknowledge("rows", id, Row(id));
            c.DatabasePath = path;
            return path;
        }

        public static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 7 % 23, ["text"] = "row-" + id };

        /// <summary>Recent committed writes, left in the WAL (acknowledged).</summary>
        public static void RecentWrites(ILiteDatabase db, TeardownCase c, int count = 3)
        {
            var rows = db.GetCollection("rows");
            for (var id = Recent; id < Recent + count; id++)
            {
                rows.Insert(Row(id));
                c.Ledger.Acknowledge("rows", id, Row(id));
            }
        }

        /// <summary>
        /// An explicit transaction with uncommitted inserts into <c>pending</c>, held open on another thread
        /// (ledgered as aborted). Its own collection keeps the case's later writes to <c>rows</c> unblocked
        /// in Direct mode; in Shared mode it holds the writer mutex, so build it after everything else.
        /// </summary>
        public static void PendingTransaction(ILiteDatabase db, TeardownCase c)
        {
            TeardownParticipant.Start(c, "pending-transaction", () =>
            {
                db.BeginTrans();
                for (var id = Pending; id < Pending + 3; id++) db.GetCollection("pending").Insert(Row(id));
            }, () => db.Rollback());
            for (var id = Pending; id < Pending + 3; id++) c.Ledger.Abort("pending", id, null);
        }

        /// <summary>A query reader, partially read, held open on another thread.</summary>
        public static void OpenReader(ILiteDatabase db, TeardownCase c, string collection = "many")
        {
            IEnumerator<BsonDocument> reader = null;
            TeardownParticipant.Start(c, "open-reader", () =>
            {
                reader = db.GetCollection(collection).Query().ToEnumerable().GetEnumerator();
                reader.MoveNext();
            }, () => reader.Dispose());
        }

        /// <summary>
        /// A sorted reader whose sort spilled to disk, partially read on the calling thread; checks
        /// ScratchLive when the engine keeps its scratch in the <c>-tmp</c> file (Direct mode).
        /// </summary>
        public static IEnumerator<BsonDocument> SpilledReader(ILiteDatabase db, TeardownCase c, bool fileScratch)
        {
            var reader = db.GetCollection("big").Query().OrderBy("key").ToEnumerable().GetEnumerator();
            reader.MoveNext();
            if (fileScratch)
            {
                var missing = QuiescentProbe.ScratchLive(c.DatabasePath);
                if (missing != null) c.Violations.Add("scratch-live." + missing);
            }
            c.Defer(() => { try { reader.Dispose(); } catch (Exception) { /* its engine may be gone */ } });
            return reader;
        }

        private static string Build(string key, TeardownPrior prior, string password)
        {
            Directory.CreateDirectory(TemplateRoot);
            var path = Path.Combine(TemplateRoot, key + ".db");
            using (var db = new LiteDatabase(new ConnectionString { Filename = path, Password = password }))
            {
                db.GetCollection("rows").InsertBulk(Enumerable.Range(1, prior.Documents).Select(Row));
                db.GetCollection("many").InsertBulk(Enumerable.Range(1, ManyCount)
                    .Select(id => new BsonDocument { ["_id"] = id, ["n"] = id }));
                if (prior.SpilledSort)
                {
                    db.GetCollection("big").InsertBulk(Enumerable.Range(1, BigCount).Select(id => new BsonDocument
                    {
                        ["_id"] = id, ["key"] = ((id * 7919) % BigCount).ToString("D6") + new string('k', 760)
                    }));
                }
                if (prior.Upload)
                {
                    var bytes = Enumerable.Range(0, 40000).Select(index => (byte)(index * 31)).ToArray();
                    using (var source = new MemoryStream(bytes)) db.FileStorage.Upload("upload", "upload.bin", source);
                }
                db.Checkpoint();
            }
            return path;
        }
    }
}
