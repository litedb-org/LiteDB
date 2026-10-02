using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Engine;
using LiteDB.Tests.Safety;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// The independent model of one database file: what the explorer acknowledged (an operation
    /// returned, or its unit committed), what is uncertain (the operation raced a close, a fatal
    /// stop or failed after it may have committed), and the fixture. No snapshot of the
    /// implementation is consulted; <see cref="VerifyCold"/> reopens the file twice.
    /// </summary>
    internal sealed class ExplorerModel
    {
        internal const string Password = "explorer-password";
        internal static readonly string[] Collections = { "rows", "other" };
        private readonly Dictionary<string, Dictionary<int, int>> _committed = new Dictionary<string, Dictionary<int, int>>();
        // Possible values of an uncertain id: its acknowledged state before the write (null = absent) or the written value.
        private readonly Dictionary<string, Dictionary<int, HashSet<int?>>> _uncertain = new Dictionary<string, Dictionary<int, HashSet<int?>>>();
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _uncertainFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        internal ExplorerModel(string path, ExplorerMode mode, bool encrypted)
        {
            this.Path = System.IO.Path.GetFullPath(path);
            this.Mode = mode;
            this.Encrypted = encrypted;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(this.Path));
            using (var db = this.Open())
            {
                db.Timeout = ExplorerSchedule.PragmaTimeout;
                foreach (var collection in Collections)
                {
                    db.GetCollection(collection).EnsureIndex("value");
                    db.GetCollection(collection).Insert(Row(1, 10));
                    _committed.Add(collection, new Dictionary<int, int> { [1] = 10 });
                    _uncertain.Add(collection, new Dictionary<int, HashSet<int?>>());
                }
                db.GetCollection("sentinel").Insert(Row(42, 900));
            }
            // The lock-bound deadlines assume this TIMEOUT pragma; prove the fixture persisted it.
            using (var db = this.Open())
                Require(db.Timeout == ExplorerSchedule.PragmaTimeout, "fixture-timeout", "the fixture's TIMEOUT pragma is " + db.Timeout);
        }

        internal string Path { get; }
        internal ExplorerMode Mode { get; }
        internal bool Encrypted { get; }

        internal EngineSettings Settings(Func<string, BsonValue, BsonValue> readTransform = null) => new EngineSettings
        {
            Filename = this.Path, Password = this.Encrypted ? Password : null, TransactionPageLimit = 1,
            ReadTransform = readTransform
        };

        /// <summary>A new connection (Direct: its own LiteEngine; Shared: its own SharedEngine).</summary>
        internal LiteDatabase Open(Func<string, BsonValue, BsonValue> readTransform = null)
        {
            var settings = this.Settings(readTransform);
            ILiteEngine engine = this.Mode == ExplorerMode.Shared ? new SharedEngine(settings) : (ILiteEngine)new LiteEngine(settings);
            var db = new LiteDatabase(engine);
            db.CheckpointSize = 0;
            return db;
        }

        internal static BsonDocument Row(int id, int value) => new BsonDocument
        { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 8192) };

        internal int? Committed(string collection, int id)
        {
            lock (_gate) return this.Ensure(collection).TryGetValue(id, out var value) ? value : (int?)null;
        }

        /// <summary>The acknowledged rows of <paramref name="collection"/>; a collection first named here starts empty.</summary>
        private Dictionary<int, int> Ensure(string collection)
        {
            if (!_committed.TryGetValue(collection, out var rows))
            {
                _committed.Add(collection, rows = new Dictionary<int, int>());
                _uncertain.Add(collection, new Dictionary<int, HashSet<int?>>());
            }
            return rows;
        }

        internal void Acknowledge(string collection, int id, int value)
        {
            lock (_gate)
            {
                this.Ensure(collection)[id] = value;
                _uncertain[collection].Remove(id);
            }
        }

        internal void Deleted(string collection, int id)
        {
            lock (_gate)
            {
                this.Ensure(collection).Remove(id);
                _uncertain[collection].Remove(id);
            }
        }

        /// <summary>The write of <paramref name="value"/> (null: a delete) may or may not have taken effect.</summary>
        internal void Uncertain(string collection, int id, int? value)
        {
            lock (_gate)
            {
                var committed = this.Ensure(collection);
                if (!_uncertain[collection].TryGetValue(id, out var values))
                {
                    _uncertain[collection][id] = values = new HashSet<int?>();
                    values.Add(committed.TryGetValue(id, out var before) ? before : (int?)null);
                }
                values.Add(value);
            }
        }

        /// <summary>An upload that returned (ordinary) or committed (unit): the file holds exactly <paramref name="content"/>.</summary>
        internal void AcknowledgeFile(string id, byte[] content)
        {
            lock (_gate)
            {
                _files[id] = content;
                _uncertainFiles.Remove(id);
            }
        }

        /// <summary>An upload whose outcome is unknown: the file is absent or holds exactly <paramref name="content"/>.</summary>
        internal void UncertainFile(string id, byte[] content)
        {
            lock (_gate) if (!_files.ContainsKey(id)) _uncertainFiles[id] = content;
        }

        internal static void Require(bool condition, string check, string message)
        {
            if (!condition) throw new ExplorerFailure("EXPLORER_" + ExplorerFailure.Safe(check), message);
        }

        /// <summary>
        /// Cold check after every connection closed: exact ids and values (uncertain ids may show
        /// any of their possible values), byte-exact payloads, the index catalog, indexed lookups
        /// and the untouched sentinel. The first reopen also runs the Durable oracle.
        /// </summary>
        internal void VerifyCold(IExplorerHost host)
        {
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using (var db = this.Open())
                {
                    if (reopen == 0) host.Durable(this.Ledger(), db, "cold reopen of " + System.IO.Path.GetFileName(this.Path));
                    string[] names;
                    lock (_gate) names = _committed.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                    foreach (var name in names) this.VerifyCollection(db, name);
                    this.VerifyFiles(db);
                    var sentinels = db.GetCollection("sentinel").FindAll().ToArray();
                    Require(sentinels.Length == 1 && Same(sentinels[0], Row(42, 900)), "cold-sentinel", "the untouched sentinel changed");
                }
            }
        }

        private void VerifyCollection(LiteDatabase db, string name)
        {
            Dictionary<int, int> committed;
            Dictionary<int, HashSet<int?>> uncertain;
            lock (_gate)
            {
                committed = new Dictionary<int, int>(_committed[name]);
                uncertain = _uncertain[name].ToDictionary(pair => pair.Key, pair => new HashSet<int?>(pair.Value));
            }
            var rows = db.GetCollection(name);
            var actual = rows.FindAll().ToDictionary(row => row["_id"].AsInt32, row => row);
            foreach (var id in actual.Keys.Union(committed.Keys).Union(uncertain.Keys).OrderBy(id => id))
            {
                var found = actual.TryGetValue(id, out var row) ? row["value"].AsInt32 : (int?)null;
                var permitted = uncertain.TryGetValue(id, out var values) ? values
                    : new HashSet<int?> { committed.TryGetValue(id, out var value) ? value : (int?)null };
                Require(permitted.Contains(found), "cold-exact-state",
                    $"{name}/{id} is {Show(found)}; permitted {string.Join("|", permitted.Select(Show))}");
                if (row != null) Require(Same(row, Row(id, found.Value)), "cold-exact-payload", $"{name}/{id} payload differs");
            }
            var indexes = new List<string>();
            using (var reader = db.Execute("select name from $indexes where collection = @0", new BsonValue[] { name }))
                while (reader.Read()) indexes.Add(reader.Current["name"].AsString);
            // Fixture collections carry the value index; collections a scenario creates by writing carry only _id.
            var fixture = Collections.Contains(name);
            Require(indexes.OrderBy(x => x).SequenceEqual(fixture ? new[] { "_id", "value" } : actual.Count == 0 ? new string[0] : new[] { "_id" }),
                "cold-index-catalog", $"{name} indexes are {string.Join(",", indexes)}");
            if (!fixture) return;
            foreach (var value in actual.Values.Select(row => row["value"].AsInt32).Distinct())
            {
                var indexed = rows.Find(Query.EQ("value", value)).Select(row => row["_id"].AsInt32).OrderBy(id => id);
                var scanned = actual.Where(pair => pair.Value["value"].AsInt32 == value).Select(pair => pair.Key).OrderBy(id => id);
                Require(indexed.SequenceEqual(scanned), "cold-indexed-state", $"{name} value={value}: index and scan disagree");
            }
        }

        private void VerifyFiles(LiteDatabase db)
        {
            KeyValuePair<string, byte[]>[] files, uncertain;
            lock (_gate)
            {
                files = _files.ToArray();
                uncertain = _uncertainFiles.ToArray();
            }
            foreach (var file in files.Select(pair => (pair, required: true)).Concat(uncertain.Select(pair => (pair, required: false))))
            {
                var info = db.FileStorage.FindById(file.pair.Key);
                Require(info != null || !file.required, "cold-file-lost", $"acknowledged file {file.pair.Key} is missing");
                if (info == null) continue;
                using (var stream = new MemoryStream())
                {
                    info.CopyTo(stream);
                    Require(stream.ToArray().SequenceEqual(file.pair.Value), "cold-file-content", $"file {file.pair.Key} content differs");
                }
            }
        }

        /// <summary>Acknowledged effects as an M1 <see cref="DurableLedger"/> (uncertain ids are not recorded).</summary>
        internal DurableLedger Ledger()
        {
            var ledger = new DurableLedger();
            lock (_gate)
            {
                foreach (var collection in _committed)
                    foreach (var row in collection.Value)
                        if (!_uncertain[collection.Key].ContainsKey(row.Key)) ledger.Acknowledge(collection.Key, row.Key, Row(row.Key, row.Value));
            }
            return ledger;
        }

        private static string Show(int? value) => value.HasValue ? value.Value.ToString() : "absent";

        private static bool Same(BsonDocument left, BsonDocument right) =>
            BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right));
    }
}
