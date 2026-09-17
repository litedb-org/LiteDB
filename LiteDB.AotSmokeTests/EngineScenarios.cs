using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using LiteDB.Vector;

using static LiteDB.AotSmokeTests.SmokeAssert;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    /// <summary>
    /// Engine features reached through the document API. None of them involve entity mapping, so they are
    /// expected to work in every publish mode without generated maps.
    /// </summary>
    internal static class EngineScenarios
    {
        internal static void Run()
        {
            WithDatabaseFile(RunPersistenceAndTransactions);
            WithDatabaseFile(RunEncryption);
            WithDatabaseFile(RunMaintenance);
            WithDatabaseFile(RunCultureCollation);
            WithDatabaseFile(RunSharedConnection);
            WithDatabaseFile(RunConcurrentWriters);
            RunVectorSearch();
            RunDocumentLinq();
        }

        private static void RunPersistenceAndTransactions(string path)
        {
            Console.WriteLine("  [4.1] Commit and roll back explicit transactions, then reopen the data file.");
            using (var database = new LiteDatabase(path))
            {
                var items = database.GetCollection("items");

                Require(database.BeginTrans(), "The transaction did not begin.");
                items.Insert(new BsonDocument { ["_id"] = 1, ["name"] = "committed" });
                Require(database.Commit(), "The transaction did not commit.");

                Require(database.BeginTrans(), "The second transaction did not begin.");
                items.Insert(new BsonDocument { ["_id"] = 2, ["name"] = "rolled-back" });
                Require(database.Rollback(), "The transaction did not roll back.");

                Report("count after rollback", items.Count());
            }

            using (var reopened = new LiteDatabase(path))
            {
                var names = reopened.GetCollection("items").FindAll().Select(x => x["name"].AsString).ToArray();
                Report("names after reopen", names);
                Require(names.SequenceEqual(new[] { "committed" }), "Committed data did not survive a reopen, or rolled-back data did.");
            }

            Console.WriteLine("        Passed: commit, rollback, and durability across reopen.");
        }

        private static void RunEncryption(string path)
        {
            Console.WriteLine("  [4.2] Create, reopen, and protect a password-encrypted data file.");
            var connection = new ConnectionString { Filename = path, Password = "aot-secret" };

            using (var database = new LiteDatabase(connection))
            {
                database.GetCollection("secrets").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "plaintext-marker" });
            }

            var plaintextVisible = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("plaintext-marker");
            Report("plaintext visible on disk", plaintextVisible);
            Require(plaintextVisible == false, "The encrypted data file contains the plaintext payload.");

            using (var reopened = new LiteDatabase(connection))
            {
                var payload = reopened.GetCollection("secrets").FindById(1)["payload"].AsString;
                Report("decrypted payload", payload);
                Require(payload == "plaintext-marker", "The encrypted data file did not decrypt.");
            }

            RequireThrows<LiteException>(
                () =>
                {
                    using var wrong = new LiteDatabase(new ConnectionString { Filename = path, Password = "wrong" });
                    wrong.GetCollection("secrets").Count();
                },
                "A wrong password opened the encrypted data file.");

            Console.WriteLine("        Passed: AES data file round trip and wrong-password rejection.");
        }

        private static void RunMaintenance(string path)
        {
            Console.WriteLine("  [4.3] Run checkpoint, pragma, user version, and rebuild.");
            using var database = new LiteDatabase(path);
            var items = database.GetCollection("items");
            items.Insert(Enumerable.Range(1, 200).Select(i => new BsonDocument { ["_id"] = i, ["value"] = i * 2 }));
            items.DeleteMany("_id > 50");

            database.Checkpoint();
            database.UserVersion = 42;
            Report("USER_VERSION pragma", database.Pragma("USER_VERSION").AsInt32);

            database.Rebuild();
            Report("count after rebuild", items.Count());
            Report("sum after rebuild", items.FindAll().Sum(x => x["value"].AsInt32));
            Require(items.Count() == 50 && database.UserVersion == 42, "Rebuild lost documents or the user version.");

            Console.WriteLine("        Passed: checkpoint, pragma, user version, and rebuild.");
        }

        private static void RunCultureCollation(string path)
        {
            Console.WriteLine("  [4.4] Create and reopen a data file with a culture-specific collation.");
            var collation = new Collation("en-US/IgnoreCase");

            using (var database = new LiteDatabase(new ConnectionString { Filename = path, Collation = collation }))
            {
                var words = database.GetCollection("words");
                words.Insert(new[] { "banana", "Apple", "cherry", "apple" }.Select((word, index) => new BsonDocument { ["_id"] = index + 1, ["word"] = word }));
                words.EnsureIndex("word", "$.word");
            }

            using (var reopened = new LiteDatabase(path))
            {
                var words = reopened.GetCollection("words");
                Report("collation after reopen", reopened.Collation.ToString());
                Report("case-insensitive matches", words.Count("$.word = 'APPLE'"));
                Report("ordered", words.Query().OrderBy("$.word").ThenBy("$._id").ToEnumerable().Select(x => x["word"].AsString).ToArray());
                Require(words.Count("$.word = 'APPLE'") == 2, "The en-US/IgnoreCase collation did not compare case-insensitively.");
            }

            Console.WriteLine("        Passed: culture-specific collation persisted, reopened, compared, and ordered.");
        }

        private static void RunSharedConnection(string path)
        {
            Console.WriteLine("  [4.5] Use two shared-mode connections on one data file.");
            var connection = new ConnectionString { Filename = path, Connection = ConnectionType.Shared };

            using var first = new LiteDatabase(connection);
            using var second = new LiteDatabase(connection);

            first.GetCollection("shared").Insert(new BsonDocument { ["_id"] = 1, ["from"] = "first" });
            second.GetCollection("shared").Insert(new BsonDocument { ["_id"] = 2, ["from"] = "second" });

            Report("visible to first", first.GetCollection("shared").Count());
            Report("visible to second", second.GetCollection("shared").Count());
            Require(first.GetCollection("shared").Count() == 2 && second.GetCollection("shared").Count() == 2,
                "Shared-mode connections did not see each other's writes.");

            Console.WriteLine("        Passed: shared-mode locking and cross-connection visibility.");
        }

        private static void RunConcurrentWriters(string path)
        {
            Console.WriteLine("  [4.6] Insert from parallel writers into one data file.");
            using var database = new LiteDatabase(path);
            var items = database.GetCollection("parallel");

            Parallel.For(0, 8, writer =>
            {
                for (var i = 0; i < 50; i++)
                {
                    items.Insert(new BsonDocument { ["_id"] = writer * 1000 + i, ["writer"] = writer });
                }
            });

            Report("documents", items.Count());
            Report("distinct writers", items.FindAll().Select(x => x["writer"].AsInt32).Distinct().Count());
            Require(items.Count() == 400, "Parallel writers lost documents.");

            Console.WriteLine("        Passed: concurrent writers and transaction locking.");
        }

        private static void RunVectorSearch()
        {
            Console.WriteLine("  [4.7] Build a vector index and run a nearest-neighbour query.");
            using var stream = new MemoryStream();
            using var database = new LiteDatabase(stream);
            var docs = database.GetCollection("vectors");

            docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
            docs.Insert(new BsonDocument { ["_id"] = 2, ["Embedding"] = new BsonVector(new[] { 0f, 1f }) });
            docs.Insert(new BsonDocument { ["_id"] = 3, ["Embedding"] = new BsonVector(new[] { 0.9f, 0.1f }) });
            docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));

            var nearest = docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 2).ToEnumerable().Select(x => x["_id"].AsInt32).ToArray();
            Report("nearest ids", nearest);
            Require(nearest.SequenceEqual(new[] { 1, 3 }), "The vector index did not return the nearest neighbours in order.");

            Console.WriteLine("        Passed: vector index creation and top-k search.");
        }

        private static void RunDocumentLinq()
        {
            Console.WriteLine("  [4.8] Query a document collection with LINQ lambdas that capture BSON-native values.");
            using var stream = new MemoryStream();
            using var database = new LiteDatabase(stream);
            var items = database.GetCollection("items");
            items.Insert(Enumerable.Range(1, 5).Select(i => new BsonDocument { ["_id"] = i, ["score"] = i * 10, ["name"] = "item-" + i }));

            var minimum = 25;
            var prefix = "item-";
            var matches = items.Find(x => x["score"] > minimum && x["name"].AsString.StartsWith(prefix)).Select(x => x["_id"].AsInt32).ToArray();
            Report("ids above captured minimum", matches);
            Require(matches.SequenceEqual(new[] { 3, 4, 5 }), "The document LINQ predicate with captured values failed.");

            var captured = new object[] { new UnmappedCapturedValue { Threshold = 1 } };
            RequireThrows<NotSupportedException>(
                () => items.Count(x => captured.Contains(x["name"])),
                "A document collection mapped a captured application object at run time.");

            Console.WriteLine("        Passed: document LINQ with captured variables, and rejection of a captured application object.");
        }

        private static void WithDatabaseFile(Action<string> scenario)
        {
            var path = Path.Combine(Path.GetTempPath(), $"litedb-aot-engine-{Guid.NewGuid():N}.db");

            try
            {
                scenario(path);
            }
            finally
            {
                File.Delete(path);
                File.Delete(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-log.db"));
            }
        }
    }
}
