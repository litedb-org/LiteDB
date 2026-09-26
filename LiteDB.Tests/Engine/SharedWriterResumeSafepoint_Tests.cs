#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedWriterResumeSafepoint_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Resume_cannot_reclaim_intermediate_committed_safepoints_without_their_own_witnesses(string password)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-resume-safepoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            var passed = false;
            try
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password, TransactionPageLimit = 3 }))
                using (var writer = new LiteDatabase(engine))
                using (var reader = Open(file, password))
                using (var peer = Open(file, password))
                {
                    var held = new List<IEnumerator<BsonDocument>>();
                    try
                    {
                        for (var revision = 0; revision < 3; revision++)
                        {
                            Write(writer, revision);
                            var snapshot = reader.GetCollection("rows").Query().OrderBy("_id").ToEnumerable().GetEnumerator();
                            snapshot.MoveNext().Should().BeTrue();
                            held.Add(snapshot);
                        }
                        Write(writer, 3);
                        Write(peer, 4);
                        Write(writer, 5);
                        writer.Checkpoint();
                        Preserve(file, directory, "before-uncommitted");
                        var resumed = engine.WriterResumeCount;
                        writer.BeginTrans().Should().BeTrue();
                        writer.GetCollection("rows").DeleteAll().Should().Be(200);
                        writer.GetCollection("witness").DeleteAll().Should().Be(1);
                        engine.WriterResumeCount.Should().BeGreaterThan(resumed, "this must exercise the imported metadata");
                        Preserve(file, directory, "before-rollback");
                        writer.Rollback().Should().BeTrue();
                        using (var fresh = Open(file, password)) Verify(fresh, 5);
                        for (var revision = 0; revision < held.Count; revision++)
                        {
                            var id = 0;
                            do
                            {
                                VerifyRow(held[revision].Current, id++, revision);
                            } while (held[revision].MoveNext());
                            id.Should().Be(200);
                        }
                    }
                    finally { foreach (var snapshot in held) snapshot.Dispose(); }
                }
                for (var attempt = 0; attempt < 2; attempt++)
                    using (var cold = Open(file, password)) Verify(cold, 5);
                passed = true;
            }
            finally
            {
                if (passed) Directory.Delete(directory, true);
                else Console.Error.WriteLine("Preserved safepoint failure: " + directory);
            }
        }

        private static LiteDatabase Open(string file, string password) => new LiteDatabase(new ConnectionString
            { Filename = file, Password = password, Connection = ConnectionType.Shared, TransactionPageLimit = 3 });

        private static void Write(LiteDatabase db, int revision)
        {
            db.BeginTrans().Should().BeTrue();
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("revision");
            for (var id = 0; id < 200; id++)
            {
                rows.Delete(id);
                rows.Insert(Row(id, revision));
            }
            db.GetCollection("witness").Upsert(new BsonDocument { ["_id"] = 1, ["revision"] = revision });
            db.Commit().Should().BeTrue();
        }

        private static BsonDocument Row(int id, int revision) => new BsonDocument
        {
            ["_id"] = id, ["revision"] = revision, ["payload"] = new string((char)('a' + id % 26), 4000) + ":" + revision
        };

        private static void Verify(LiteDatabase db, int revision)
        {
            var rows = db.GetCollection("rows");
            foreach (var documents in new[] { rows.FindAll(), rows.Find(Query.EQ("revision", revision)) })
            {
                var ordered = documents.OrderBy(row => row["_id"].AsInt32).ToArray();
                ordered.Length.Should().Be(200);
                for (var id = 0; id < 200; id++) VerifyRow(ordered[id], id, revision);
            }
            db.GetCollection("witness").FindById(1)["revision"].AsInt32.Should().Be(revision);
        }

        private static void VerifyRow(BsonDocument row, int id, int revision)
        {
            row.Count.Should().Be(3);
            row["_id"].AsInt32.Should().Be(id);
            row["revision"].AsInt32.Should().Be(revision);
            row["payload"].AsString.Should().Be(Row(id, revision)["payload"].AsString);
        }

        private static void Preserve(string file, string directory, string suffix)
        {
            foreach (var path in new[] { file, FileHelper.GetLogFile(file) })
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var target = new FileStream(Path.Combine(directory, Path.GetFileName(path) + "." + suffix), FileMode.CreateNew, FileAccess.Write))
                    source.CopyTo(target);
        }
    }
}
#endif
