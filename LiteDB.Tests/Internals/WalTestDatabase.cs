using System;
using System.IO;
using System.Linq;
using LiteDB.Engine;

namespace LiteDB.Internals
{
    /// <summary>Captures recoverable stream images without disposing the original engine.</summary>
    internal sealed class WalTestDatabase : IDisposable
    {
        internal const int DocumentCount = 16;
        private readonly string _password;
        internal MemoryStream Data { get; } = new MemoryStream();
        internal MemoryStream Log { get; } = new MemoryStream();
        internal LiteEngine Engine { get; }
        internal LiteDatabase Database { get; }

        internal WalTestDatabase(string password)
        {
            _password = password;
            this.Engine = new LiteEngine(new EngineSettings
            {
                DataStream = this.Data, LogStream = this.Log, Password = password, TransactionPageLimit = 1
            });
            this.Database = new LiteDatabase(this.Engine, disposeOnClose: false);
            this.Database.Pragma(Pragmas.CHECKPOINT, 0);
        }

        internal void Seed(string collection)
        {
            this.Database.GetCollection(collection).Insert(Enumerable.Range(0, DocumentCount).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = 0, ["payload"] = new string('x', 1500) }));
        }

        internal void Update(string collection, int value)
        {
            this.Database.GetCollection(collection).Update(Enumerable.Range(0, DocumentCount).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500) }));
        }

        internal BsonDocument[] Recover(string collection, bool checkpoint)
        {
            // Clone before opening the recovery engine: normal close/rollback
            // must not repair the original bytes before this assertion.
            using var data = Copy(this.Data);
            using var log = Copy(this.Log);
            using (var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = _password
            }))
            using (var database = new LiteDatabase(engine, disposeOnClose: false))
            {
                if (!checkpoint) return database.GetCollection(collection).FindAll().ToArray();
                database.Checkpoint();
            }
            // A second open verifies the data-file image produced by checkpoint.
            using var reopenedEngine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = _password
            });
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            return reopened.GetCollection(collection).FindAll().ToArray();
        }

        private static MemoryStream Copy(MemoryStream source)
        {
            var copy = new MemoryStream();
            var bytes = source.ToArray();
            copy.Write(bytes, 0, bytes.Length);
            copy.Position = 0;
            return copy;
        }

        public void Dispose()
        {
            this.Database.Dispose();
            this.Engine.Dispose();
            this.Log.Dispose();
            this.Data.Dispose();
        }
    }
}
