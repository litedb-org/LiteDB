using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactWriteLifetime_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Rollback_failed_write_and_drop_replace_the_preparation_catalog(string password)
        {
            using var file = new TempFile();
            var settings = new EngineSettings
            {
                Filename = file.Filename, Password = password, TransactionPageLimit = 2,
                CompactStorage = CompactStorageMode.Compact
            };
            var originals = Enumerable.Range(1, 8).Select(i => Document(i, "Original")).ToArray();
            var replacements = Enumerable.Range(21, 8).Select(i => Document(i, "Replacement")).ToArray();
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 1, ["Keep"] = "unchanged" });
                var docs = db.GetCollection("docs");
                docs.EnsureIndex("Marker", true);
                docs.Insert(originals);
                db.Checkpoint();

                db.BeginTrans().Should().BeTrue();
                docs.Insert(Enumerable.Range(9, 12).Select(i => Document(i, "RolledBack")));
                db.Rollback().Should().BeTrue();
                Verify(db, originals);

                // The unique-key failure happens after data/schema preparation and must
                // discard the snapshot's reused writer along with the transaction.
                var conflict = Document(10, "FailedShape");
                conflict["Marker"] = 1;
                Action insert = () => docs.Insert(new[] { Document(9, "FailedShape"), conflict });
                insert.Should().Throw<LiteException>();
                Verify(db, originals);

                docs.Insert(replacements);
                Verify(db, originals.Concat(replacements));
                db.Checkpoint();
                db.DropCollection("docs").Should().BeTrue();
                // Reuse the same collection name and reclaimed pages with different schemas.
                docs = db.GetCollection("docs");
                docs.EnsureIndex("Marker", true);
                docs.Insert(replacements);
                Verify(db, replacements);
            }
            foreach (var readOnly in new[] { true, false, false })
            {
                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, Password = password, ReadOnly = readOnly
                });
                Verify(db, replacements);
                if (!readOnly) db.Checkpoint();
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Reused_preparation_before_wal_failure_recovers_only_whole_transaction(string password)
        {
            using var file = new TempFile();
            var settings = new EngineSettings
            {
                Filename = file.Filename, Password = password, TransactionPageLimit = 32,
                CompactStorage = CompactStorageMode.Compact
            };
            var originals = Enumerable.Range(1, 8).Select(i => Document(i, "Original")).ToArray();
            var added = Enumerable.Range(9, 80).Select(i => Document(i, "NewShape")).ToArray();
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 1, ["Keep"] = "unchanged" });
                var docs = db.GetCollection("docs");
                docs.EnsureIndex("Marker", true);
                docs.Insert(originals);
                db.Checkpoint();
                var writes = 0;
                var yielded = 0;
                engine.SimulateDiskWriteFail = page =>
                {
                    if (++writes == 2) throw new IOException("Injected compact write failure");
                };
                Action insert = () => docs.Insert(added.Select(document => { yielded++; return document; }));
                insert.Should().Throw<IOException>().WithMessage("Injected compact write failure");
                writes.Should().BeGreaterThanOrEqualTo(2);
                yielded.Should().BeGreaterThan(3, "several documents must use the encoder before the failing WAL write");
            }
            // Repeated cold opens must never resolve a reference with a discarded catalog.
            for (var open = 0; open < 3; open++)
            {
                using var db = new LiteDatabase(new LiteEngine(settings));
                Verify(db, originals);
                if (open == 2)
                {
                    db.GetCollection("docs").Insert(added);
                    Verify(db, originals.Concat(added));
                    db.Checkpoint();
                }
            }
            using var reopened = new LiteDatabase(new LiteEngine(settings));
            Verify(reopened, originals.Concat(added));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Unknown_confirmation_outcome_stops_writes_and_reopens_with_complete_schemas(string password)
        {
            using var file = new TempFile();
            var settings = new EngineSettings
            {
                Filename = file.Filename, Password = password, CompactStorage = CompactStorageMode.Compact
            };
            var originals = Enumerable.Range(1, 8).Select(i => Document(i, "Original")).ToArray();
            var added = Enumerable.Range(9, 8).Select(i => Document(i, "UncertainShape")).ToArray();
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 1, ["Keep"] = "unchanged" });
                var docs = db.GetCollection("docs");
                docs.EnsureIndex("Marker", true);
                docs.Insert(originals);
                db.Checkpoint();
                var hits = 0;
                var writes = 0;
                engine.SimulateDiskWriteFail = _ => writes++;
                engine.SimulateCrashPoint = phase =>
                {
                    if (phase == "wal-confirmation-after-write")
                    {
                        hits++;
                        throw new IOException("Injected unknown compact outcome");
                    }
                };
                Action insert = () => docs.Insert(added);
                insert.Should().Throw<IOException>();
                hits.Should().Be(1);
                var stoppedWrites = writes;
                Action later = () => docs.Insert(Document(99, "LaterShape"));
                later.Should().Throw<IOException>();
                writes.Should().Be(stoppedWrites, "unknown confirmation must stop further preparation/publication");
            }
            BsonDocument[] recovered = null;
            for (var open = 0; open < 3; open++)
            {
                using var db = new LiteDatabase(new LiteEngine(settings));
                if (recovered == null)
                {
                    var count = db.GetCollection("docs").Count();
                    count.Should().BeOneOf(originals.Length, originals.Length + added.Length);
                    recovered = count == originals.Length ? originals : originals.Concat(added).ToArray();
                }
                Verify(db, recovered);
                db.Checkpoint();
            }
        }

        [Fact]
        public void Encoder_leases_are_exclusive_reset_and_disposed_with_the_snapshot()
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("docs").Insert(Document(1, "Original"));
            db.BeginTrans().Should().BeTrue();
            var transaction = engine.GetMonitor().GetTransaction(false, false, out _);
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
            var first = snapshot.RentCompactWriter();
            var nested = snapshot.RentCompactWriter();
            nested.Should().NotBeSameAs(first);
            first.Encode(Document(2, "Unpublished"));
            snapshot.ReturnCompactWriter(nested);
            snapshot.ReturnCompactWriter(first);
            StreamOf(first).CanWrite.Should().BeFalse();
            var reused = snapshot.RentCompactWriter();
            reused.Should().BeSameAs(nested);
            reused.Pending.Should().BeEmpty();
            snapshot.ReturnCompactWriter(reused);
            db.Rollback().Should().BeTrue();
            StreamOf(reused).CanWrite.Should().BeFalse();
            db.BeginTrans().Should().BeTrue();
            transaction = engine.GetMonitor().GetTransaction(false, false, out _);
            snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
            var outstanding = snapshot.RentCompactWriter();
            db.Rollback().Should().BeTrue();
            snapshot.ReturnCompactWriter(outstanding);
            StreamOf(outstanding).CanWrite.Should().BeFalse();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Active_reader_keeps_its_schema_version_while_writer_reuses_and_extends_its_catalog(string password)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Password = password, CompactStorage = CompactStorageMode.Compact
            });
            db.CheckpointSize = 0;
            var docs = db.GetCollection("docs");
            var originals = Enumerable.Range(1, 8).Select(i => Document(i, "Original")).ToArray();
            var added = Enumerable.Range(9, 8).Select(i => Document(i, "NewShape")).ToArray();
            docs.Insert(originals);
            using var reader = docs.FindAll().GetEnumerator();
            reader.MoveNext().Should().BeTrue();
            var seen = new List<BsonDocument> { reader.Current };
#pragma warning disable xUnit1031 // The cursor stays on its owning thread while the competing writer completes.
            System.Threading.Tasks.Task.Run(() =>
            {
                docs.Insert(added);
                db.Checkpoint();
            }).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            while (reader.MoveNext()) seen.Add(reader.Current);
            seen.Select(d => d["_id"].AsInt32).Should().Equal(originals.Select(d => d["_id"].AsInt32));
            for (var i = 0; i < originals.Length; i++)
                BsonSerializer.Serialize(seen[i]).Should().Equal(BsonSerializer.Serialize(originals[i]));
            docs.Count().Should().Be(originals.Length + added.Length);
            foreach (var document in added)
                BsonSerializer.Serialize(docs.FindById(document["_id"]))
                    .Should().Equal(BsonSerializer.Serialize(document));
        }

        private static MemoryStream StreamOf(CompactDocumentWriter writer) =>
            (MemoryStream)typeof(CompactDocumentWriter).GetField("_stream",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(writer);

        private static BsonDocument Document(int id, string shape)
        {
            var document = new BsonDocument { ["_id"] = id, ["Marker"] = id };
            for (var field = 0; field < 20; field++) document[shape + "LongPropertyName" + field] = id + field;
            return document;
        }

        private static void Verify(LiteDatabase db, IEnumerable<BsonDocument> expected)
        {
            var wanted = expected.ToArray();
            var docs = db.GetCollection("docs");
            docs.Count().Should().Be(wanted.Length);
            foreach (var document in wanted)
            {
                BsonSerializer.Serialize(docs.FindById(document["_id"]))
                    .Should().Equal(BsonSerializer.Serialize(document));
                var indexed = docs.Find(Query.EQ("Marker", document["Marker"])).Single();
                BsonSerializer.Serialize(indexed).Should().Equal(BsonSerializer.Serialize(document));
            }
            db.GetCollection("unrelated").FindById(1)["Keep"].AsString.Should().Be("unchanged");
        }
    }
}
