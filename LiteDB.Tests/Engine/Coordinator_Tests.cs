#if !NETFRAMEWORK
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Experimental coordinator mode within one process: every engine instance is a
    /// separate participant, so the first becomes the coordinator and later ones are
    /// clients talking to it over the named pipe exactly as other processes would.
    /// </summary>
    public class Coordinator_Tests
    {
        public class Item
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int Value { get; set; }
        }

        [Fact]
        public void Clients_write_through_the_coordinator_and_read_the_files_directly()
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var client = new CoordinatedEngine(file.Filename);
            host.IsCoordinator.Should().BeTrue();
            client.IsCoordinator.Should().BeFalse();
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            using var clientDb = new LiteDatabase(client, disposeOnClose: false);

            var items = clientDb.GetCollection<Item>("items");
            var created = Enumerable.Range(0, 50).Select(i => new Item { Name = "n" + i, Value = i }).ToList();
            items.Insert(created).Should().Be(50);
            created.Select(x => x.Id).Should().OnlyHaveUniqueItems().And.NotContain(0, "generated ids reach the caller's entities");
            items.EnsureIndex(x => x.Name).Should().BeTrue();

            var before = client.DirectReads;
            items.FindOne(x => x.Name == "n7").Id.Should().Be(created[7].Id);
            client.DirectReads.Should().BeGreaterThan(before, "a read outside a transaction uses a direct snapshot");

            created[3].Value = 300;
            items.Update(created[3]).Should().BeTrue();
            items.Delete(created[4].Id).Should().BeTrue();
            items.UpdateMany(x => new Item { Id = x.Id, Name = x.Name, Value = x.Value + 1000 }, x => x.Value < 3).Should().Be(3);
            items.DeleteMany(x => x.Value == 10).Should().Be(1);
            items.Upsert(new Item { Id = created[5].Id, Name = "five", Value = 5 }).Should().BeFalse();
            var fresh = new Item { Name = "fresh", Value = 99 };
            items.Upsert(fresh).Should().BeTrue();
            fresh.Id.Should().NotBe(0);

            var viaClient = items.FindAll().OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Name}:{x.Value}").ToList();
            var viaHost = hostDb.GetCollection<Item>("items").FindAll().OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Name}:{x.Value}").ToList();
            viaClient.Should().Equal(viaHost);
            viaClient.Should().HaveCount(49);
            clientDb.GetCollectionNames().Should().Contain("items");
        }

        [Fact]
        public void Client_transactions_commit_and_roll_back_on_the_coordinator()
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var client = new CoordinatedEngine(file.Filename);
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            using var clientDb = new LiteDatabase(client, disposeOnClose: false);
            var clientDocs = clientDb.GetCollection("docs");
            var hostDocs = hostDb.GetCollection("docs");

            clientDb.BeginTrans().Should().BeTrue();
            clientDocs.Insert(Enumerable.Range(1, 3).Select(i => new BsonDocument { ["_id"] = i }));
            clientDocs.Count().Should().Be(3, "a transaction reads its own writes through the coordinator");
            hostDocs.Count().Should().Be(0);
            clientDb.Rollback().Should().BeTrue();
            clientDocs.Count().Should().Be(0);

            clientDb.BeginTrans().Should().BeTrue();
            clientDocs.Insert(new BsonDocument { ["_id"] = 10 });
            clientDb.Commit().Should().BeTrue();
            (hostDocs.FindById(10) != null).Should().BeTrue();
            (clientDocs.FindById(10) != null).Should().BeTrue();
        }

        [Fact]
        public void A_disconnected_client_aborts_its_transaction()
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            using (var client = new CoordinatedEngine(file.Filename))
            using (var clientDb = new LiteDatabase(client, disposeOnClose: false))
            {
                clientDb.BeginTrans();
                clientDb.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            }

            var docs = hostDb.GetCollection("docs");
            docs.Count().Should().Be(0);
            // The collection lock was released with the session.
            docs.Insert(new BsonDocument { ["_id"] = 2 });
            docs.Count().Should().Be(1);
        }

        [Fact]
        public void A_direct_snapshot_keeps_its_version_while_the_coordinator_writes_and_checkpoints()
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var client = new CoordinatedEngine(file.Filename);
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            var docs = hostDb.GetCollection("docs");
            docs.Insert(Enumerable.Range(0, 500).Select(i => new BsonDocument { ["_id"] = i, ["v"] = 0, ["pad"] = new string('x', 400) }));

            var before = client.DirectReads;
            var seen = 0;
            using (var reader = client.Query("docs", new Query()))
            {
                client.DirectReads.Should().Be(before + 1);
                for (; seen < 100 && reader.Read(); seen++) reader.Current["v"].AsInt32.Should().Be(0);
                docs.UpdateMany("{ _id: $._id, v: 1, pad: $.pad }", "true").Should().Be(500);
                hostDb.Checkpoint();
                while (reader.Read())
                {
                    reader.Current["v"].AsInt32.Should().Be(0, "the reader keeps its registered snapshot");
                    seen++;
                }
            }
            seen.Should().Be(500);
            var opens = client.SnapshotOpens;
            for (var i = 0; i < 3; i++)
            {
                using var reader = client.Query("docs", new Query());
                while (reader.Read()) reader.Current["v"].AsInt32.Should().Be(1);
            }
            client.SnapshotOpens.Should().Be(opens + 1, "without commits in between the cached snapshot is reused");

            // The idle snapshot releases its lease; then the WAL can be checkpointed away.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (client.HasCachedSnapshot && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(20);
            client.HasCachedSnapshot.Should().BeFalse("an idle cached snapshot is released");
            hostDb.Checkpoint();
            var log = FileHelper.GetLogFile(file.Filename);
            (File.Exists(log) ? new FileInfo(log).Length : 0).Should().Be(0, "no lease remains, so the WAL is checkpointed away");
        }

        [Fact]
        public void A_crashed_coordinator_is_replaced_and_acknowledged_writes_survive()
        {
            using var file = new TempFile();
            using var first = new CoordinatedEngine(file.Filename);
            using var second = new CoordinatedEngine(file.Filename);
            using var firstDb = new LiteDatabase(first, disposeOnClose: false);
            using var secondDb = new LiteDatabase(second, disposeOnClose: false);
            firstDb.BeginTrans();
            firstDb.GetCollection("pending").Insert(new BsonDocument { ["_id"] = "uncommitted" });
            var docs = secondDb.GetCollection("docs");
            for (var i = 0; i < 50; i++) docs.Insert(new BsonDocument { ["_id"] = i });

            first.CrashCoordinator();
            var acknowledged = Enumerable.Range(0, 50).ToList();
            var unknown = new List<int>();
            for (var i = 50; i < 60; i++)
            {
                try
                {
                    docs.Insert(new BsonDocument { ["_id"] = i });
                    acknowledged.Add(i);
                }
                catch (LiteException ex) when (ex.Message.Contains("outcome is unknown"))
                {
                    unknown.Add(i);
                }
            }
            unknown.Should().HaveCountLessOrEqualTo(1, "only the call in flight when the pipe broke is ambiguous");
            second.IsCoordinator.Should().BeTrue("the surviving client took over");

            Action continueTransaction = () => firstDb.GetCollection("pending").Insert(new BsonDocument { ["_id"] = "late" });
            continueTransaction.Should().Throw<LiteException>().WithMessage("*transaction was aborted*");
            var ids = firstDb.GetCollection("docs").FindAll().Select(x => x["_id"].AsInt32).ToList();
            ids.Should().Contain(acknowledged);
            ids.Except(acknowledged).Should().BeSubsetOf(unknown);
            (secondDb.GetCollection("pending").FindById("uncommitted") == null).Should().BeTrue();
        }

        [Fact]
        public void A_snapshot_grant_waits_for_running_calls_and_holds_back_new_ones()
        {
            var gate = new LiteDB.Client.Coordinated.CoordinatorGate();
            gate.Enter();
            gate.TryClose(TimeSpan.FromMilliseconds(50)).Should().BeFalse("a call is still running");
            gate.Exit();
            gate.TryClose(TimeSpan.FromMilliseconds(50)).Should().BeTrue();
            var entered = new System.Threading.ManualResetEventSlim();
            var caller = new System.Threading.Thread(() =>
            {
                gate.Enter();
                entered.Set();
                gate.Exit();
            });
            caller.Start();
            entered.Wait(TimeSpan.FromMilliseconds(200)).Should().BeFalse("new calls wait while the client opens its snapshot");
            gate.Open();
            entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            caller.Join();
        }

        [Fact]
        public void A_busy_coordinator_answers_reads_over_ipc_instead()
        {
            using var file = new TempFile();
            using var host = new CoordinatedEngine(file.Filename);
            using var client = new CoordinatedEngine(file.Filename);
            using var hostDb = new LiteDatabase(host, disposeOnClose: false);
            using var clientDb = new LiteDatabase(client, disposeOnClose: false);
            hostDb.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["v"] = "committed" });

            // A write that stays inside its engine call keeps the coordinator busy.
            var started = new System.Threading.ManualResetEventSlim();
            var release = new System.Threading.ManualResetEventSlim();
            IEnumerable<BsonDocument> Slow()
            {
                yield return new BsonDocument { ["_id"] = 1 };
                started.Set();
                release.Wait();
            }
            var writer = System.Threading.Tasks.Task.Run(() => hostDb.GetCollection("other").Insert(Slow()));
            started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

            var ipc = client.IpcReads;
            clientDb.GetCollection("docs").FindById(1)["v"].AsString.Should().Be("committed");
            client.IpcReads.Should().Be(ipc + 1, "the snapshot grant gave up, so the read went over IPC");
            release.Set();
            writer.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            clientDb.GetCollection("other").Count().Should().Be(1);
        }

        [Fact]
        public void Coordinated_results_match_a_direct_mode_oracle()
        {
            using var file = new TempFile();
            var expected = new Dictionary<int, int>();
            var random = new Random(3006);
            using (var host = new CoordinatedEngine(file.Filename))
            using (var a = new CoordinatedEngine(file.Filename))
            using (var b = new CoordinatedEngine(file.Filename))
            {
                var databases = new[] { host, a, b }.Select(x => new LiteDatabase(x, disposeOnClose: false)).ToArray();
                databases[0].GetCollection("docs").EnsureIndex("value", "$.value");
                for (var step = 0; step < 400; step++)
                {
                    var db = databases[random.Next(databases.Length)].GetCollection("docs");
                    var id = random.Next(60);
                    var value = random.Next(1000);
                    switch (random.Next(4))
                    {
                        case 0:
                            db.Upsert(new BsonDocument { ["_id"] = id, ["value"] = value });
                            expected[id] = value;
                            break;
                        case 1:
                            db.Delete(id).Should().Be(expected.Remove(id));
                            break;
                        case 2:
                            var found = db.FindById(id);
                            if (expected.TryGetValue(id, out var stored)) found["value"].AsInt32.Should().Be(stored);
                            else (found == null).Should().BeTrue();
                            break;
                        default:
                            db.Count(Query.GT("value", 500)).Should().Be(expected.Values.Count(x => x > 500));
                            break;
                    }
                }
                foreach (var database in databases) database.Dispose();
                (a.DirectReads + b.DirectReads).Should().BeGreaterThan(0);
            }

            using var oracle = new LiteDatabase(file.Filename);
            oracle.GetCollection("docs").FindAll().ToDictionary(x => x["_id"].AsInt32, x => x["value"].AsInt32)
                .Should().Equal(expected);
        }
    }
}
#endif
