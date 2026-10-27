using System;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB;
using LiteDB.Engine;

namespace VectorCompatibility.Current
{
    internal static class BackgroundCheckpointCompatibility
    {
        internal static readonly string[] Stages = { "queued", "checkpoint-after-journal-flush", "checkpoint-after-salt-sync", "retirement-journal-retired" };

        internal static void Run(string mode, string directory)
        {
            foreach (var encrypted in new[] { false, true })
            {
                var suffix = encrypted ? "encrypted.db" : "plain.db";
                var password = encrypted ? "compatibility-test" : null;
                if (mode == "background-create")
                    foreach (var stage in Stages) Create(directory, suffix, password, stage);
                else if (mode == "background-verify")
                    foreach (var stage in Stages) Verify(Path.Combine(directory, "background-" + stage + "-" + suffix), password, 3);
                else
                    foreach (var dirty in new[] { false, true })
                        Verify(Path.Combine(directory, "before-" + (dirty ? "dirty-" : "clean-") + suffix), password, dirty ? 600 : 0);
            }
            Console.WriteLine("Background checkpoint compatibility: " + mode + " passed");
        }

        private static void Create(string directory, string suffix, string password, string stage)
        {
            var source = Path.Combine(directory, "source-background-" + stage + "-" + suffix);
            using var engine = new LiteEngine(new EngineSettings { Filename = source, Password = password, DurableCommits = false });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Enumerable.Range(0, 48).Select(Row));
            db.GetCollection("rows").EnsureIndex("value", true);
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
            db.CheckpointSize = 32;
            db.Checkpoint();
            var worker = engine.InstallDirectCheckpointLifetime();
            var pause = worker.Pause();
            using var copied = new ManualResetEventSlim();
            using var releaseReader = new ManualResetEventSlim();
            using var pinned = new ManualResetEventSlim();
            Thread reader = null;
            Exception readerFailure = null;
            var hit = 0;
            Action copy = () =>
            {
                if (Interlocked.Exchange(ref hit, 1) != 0) return;
                var target = Path.Combine(directory, "background-" + stage + "-" + suffix);
                File.Copy(source, target);
                File.Copy(LogName(source), LogName(target));
                if (new FileInfo(LogName(target)).Length % Constants.PAGE_SIZE != 0)
                    throw new Exception("Stable worker compatibility image must have aligned EOF: " + stage);
                copied.Set();
            };
            try
            {
                db.UserVersion = 1;
                if (stage == "retirement-journal-retired")
                {
                    db.GetCollection("rows").Update(Row(0));
                    reader = new Thread(() =>
                    {
                        try
                        {
                            using var cursor = engine.Query("rows", new Query());
                            if (!cursor.Read()) throw new Exception("Reader did not pin a generation");
                            pinned.Set();
                            if (!releaseReader.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Reader release");
                        }
                        catch (Exception error) { readerFailure = error; pinned.Set(); }
                    });
                    reader.Start();
                    if (!pinned.Wait(TimeSpan.FromSeconds(20)) || readerFailure != null) throw new Exception("Reader pin failed", readerFailure);
                    db.GetCollection("rows").Update(Row(0));
                }
                db.UserVersion = 3;
                db.CheckpointSize = 1;
                engine.CheckpointStage = current => { if (current == stage) copy(); };
                if (stage == "queued") copy();
                pause.Dispose();
                if (!copied.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Worker did not reach " + stage);
            }
            finally
            {
                pause.Dispose();
                releaseReader.Set();
                if (reader != null && !reader.Join(20000)) throw new TimeoutException("Reader exit");
                engine.CheckpointStage = null;
            }
            db.Checkpoint();
            if (readerFailure != null) throw readerFailure;
        }

        private static void Verify(string file, string password, int version)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var data = File.ReadAllBytes(file);
                var log = File.Exists(LogName(file)) ? File.ReadAllBytes(LogName(file)) : null;
                using (var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password, ReadOnly = true }))
                {
                    Check(db, version);
                    using var info = db.Execute("SELECT $ FROM $database");
                    if (!info.Read() || info.Current["backgroundCheckpoint"]["enabled"].AsBoolean)
                        throw new Exception("Read-only diagnostic query enabled a worker");
                }
                if (!data.SequenceEqual(File.ReadAllBytes(file)) ||
                    (log != null && !log.SequenceEqual(File.ReadAllBytes(LogName(file)))))
                    throw new Exception("Read-only recovery changed source bytes");
                using (var engine = new LiteEngine(new EngineSettings { Filename = file, Password = password, BackgroundCheckpoint = true }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    Check(db, version);
                    db.CheckpointSize = 8;
                    db.Checkpoint();
                    Check(db, version);
                }
                if (Directory.GetFiles(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + "*backup*").Length != 0)
                    throw new Exception("Compatible v6 input was forcibly rebuilt");
            }
        }

        private static void Check(LiteDatabase db, int version)
        {
            if (db.UserVersion != version || db.GetCollection("rows").Count() != 48 ||
                db.GetCollection("sentinel").FindById(1)?["payload"].AsString != "untouched")
                throw new Exception("Compatibility lost committed state");
            foreach (var id in Enumerable.Range(0, 48))
            {
                var row = db.GetCollection("rows").Find(Query.EQ("value", id * 7)).Single();
                if (row["_id"].AsInt32 != id || row["payload"].AsString != Row(id)["payload"].AsString)
                    throw new Exception("Compatibility changed indexed payload");
            }
        }

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id * 7, ["payload"] = new string((char)('a' + id % 26), 1000)
        };
        private static string LogName(string file) => Path.ChangeExtension(file, null) + "-log.db";
    }
}
