using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    // Differential observer derived from V-3137i@9bf36c0bb. No product code changes.
    [Trait("Category", "IoSafety")]
    public class Review3137k_Tests
    {
        private const int Seed = 313702;

        [Fact]
        public void Seeded_header_tears_record_recovery_and_preservation()
        {
            var root = Environment.GetEnvironmentVariable("REVIEW_3137K_DIR") ?? Path.Combine("artifacts_temp", "review3137k");
            Directory.CreateDirectory(root);
            if (!File.Exists(Path.Combine(root, "manifest.tsv"))) Generate(root);
            var only = Environment.GetEnvironmentVariable("REVIEW_3137K_STATE");
            var output = Environment.GetEnvironmentVariable("REVIEW_3137K_OUT") ?? Path.Combine(root, "results.tsv");
            using var report = new StreamWriter(output);
            report.AutoFlush = true;
            report.WriteLine("state\toutcome\tbytes_changed\tdata_changed\twal_changed\trows_correct\tindex_correct\treopen_correct\tinput_sha256\terror");
            var count = 0;
            foreach (var line in File.ReadLines(Path.Combine(root, "manifest.tsv")))
            {
                var item = line.Split('\t');
                if (only != null && item[0] != only) continue;
                using var file = new TempFile();
                var log = FileHelper.GetLogFile(file.Filename);
                var data = File.ReadAllBytes(Path.Combine(root, item[1]));
                var wal = File.ReadAllBytes(Path.Combine(root, item[2]));
                File.WriteAllBytes(file.Filename, data);
                File.WriteAllBytes(log, wal);
                var rows = false; var index = false; var reopen = false; var opened = false;
                var password = item[0].StartsWith("enc/", StringComparison.Ordinal) ? "wal-secret" : null;
                Exception error = null;
                try
                {
                    using (var db = Open(file.Filename, password))
                    {
                        opened = true;
                        Check(db, out rows, out index);
                    }
                    using (var db = Open(file.Filename, password))
                    {
                        Check(db, out var r, out var i);
                        reopen = r && i;
                    }
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    var after = File.Exists(file.Filename) ? File.ReadAllBytes(file.Filename) : Array.Empty<byte>();
                    var afterWal = File.Exists(log) ? File.ReadAllBytes(log) : Array.Empty<byte>();
                    var dc = Changed(data, after); var wc = Changed(wal, afterWal);
                    var outcome = error == null ? "OK" : (opened ? "READ_OR_REOPEN:" : "REFUSED:") +
                        ((error as LiteException)?.ErrorCode.ToString() ?? error.GetType().Name);
                    report.WriteLine(string.Join("\t", item[0], outcome, dc + wc, dc, wc, rows, index, reopen,
                        Hash(data) + ":" + Hash(wal), Clean(error?.Message)));
                    File.Delete(log);
                }
                count++;
            }
            count.Should().Be(only == null ? 8000 : 1);
        }

        private static LiteDatabase Open(string filename, string password) => new LiteDatabase(new ConnectionString
        {
            Filename = filename, Password = password
        });

        private static void Check(LiteDatabase db, out bool rows, out bool index)
        {
            var expected = new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString();
            var extra = new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" }.ToString();
            rows = string.Join("|", db.GetCollection("rows").FindAll()) == expected &&
                string.Join("|", db.GetCollection("newrows").FindAll()) == extra;
            index = string.Join("|", db.GetCollection("rows").Find(Query.EQ("_id", 1))) == expected &&
                string.Join("|", db.GetCollection("newrows").Find(Query.EQ("_id", 1))) == extra;
        }

        private static long Changed(byte[] before, byte[] after)
        {
            long changed = Math.Abs(before.Length - after.Length);
            for (var i = 0; i < Math.Min(before.Length, after.Length); i++)
                if (before[i] != after[i]) changed++;
            return changed;
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        private static string Clean(string message) => (message ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        private static void Generate(string root)
        {
            var lines = new List<string>();
            foreach (var password in new[] { null, "wal-secret" })
            {
                var kind = password == null ? "plain" : "enc";
                var folder = Path.Combine(root, kind);
                Directory.CreateDirectory(folder);
                Capture(password, out var data, out var wal, out var stale);
                var prefix = password == null ? 0 : PAGE_SIZE;
                var journalStart = wal.Length - HeaderJournal.Size;
                var journals = new Dictionary<string, byte[]>
                {
                    ["valid"] = wal,
                    ["none"] = wal.Take(journalStart).ToArray(),
                    ["stale"] = stale,
                    ["torn"] = wal.Take(wal.Length - 512).ToArray()
                };
                foreach (var pair in journals) File.WriteAllBytes(Path.Combine(folder, pair.Key + ".wal"), pair.Value);
                var random = new Random(Seed);
                var states = new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("intact", data) };
                // Physical tears: preserve either side of every sector boundary, with zero or garbage on the other side.
                for (var boundary = 0; boundary <= PAGE_SIZE; boundary += 512)
                foreach (var keepPrefix in new[] { true, false })
                foreach (var garbage in new[] { false, true })
                {
                    var damaged = (byte[])data.Clone();
                    var start = keepPrefix ? boundary : 0;
                    var end = keepPrefix ? PAGE_SIZE : boundary;
                    for (var n = start; n < end; n++) damaged[prefix + n] = garbage ? (byte)random.Next(1, 256) : (byte)0;
                    states.Add(new KeyValuePair<string, byte[]>($"raw-{boundary:D4}-{(keepPrefix ? "prefix" : "suffix")}-{(garbage ? "garbage" : "zero")}", damaged));
                }
                var fields = new[] { new[] { 0, 4 }, new[] { 4, 1 }, new[] { 14, 4 }, new[] { 31, 1 },
                    new[] { 32, 27 }, new[] { 59, 1 }, new[] { 60, 4 }, new[] { 64, 4 }, new[] { 68, 8 },
                    new[] { 76, 33 }, new[] { 109, 16 }, new[] { 125, 4 }, new[] { 191, 1 }, new[] { 192, 128 } };
                while (states.Count < 1000)
                {
                    var n = states.Count;
                    var chosen = Enumerable.Range(0, fields.Length).OrderBy(_ => random.Next()).Take(1 + n % 4).ToArray();
                    var offsets = chosen.Select(f => fields[f][0] + random.Next(fields[f][1])).ToArray();
                    var masks = offsets.Select(_ => (byte)random.Next(1, 256)).ToArray();
                    var damaged = Rewrite(data, password, bytes =>
                    {
                        for (var j = 0; j < offsets.Length; j++) bytes[offsets[j]] ^= masks[j];
                    });
                    states.Add(new KeyValuePair<string, byte[]>($"fields-{n:D4}-" + string.Join("-", offsets.Select((o, j) => o + "x" + masks[j])), damaged));
                }
                foreach (var state in states)
                {
                    var filename = kind + "/" + state.Key + ".db";
                    File.WriteAllBytes(Path.Combine(root, filename), state.Value);
                    foreach (var journal in journals.Keys)
                        lines.Add(kind + "/" + state.Key + "/" + journal + "\t" + filename + "\t" + kind + "/" + journal + ".wal");
                }
            }
            File.WriteAllLines(Path.Combine(root, "manifest.tsv"), lines);
        }

        private static void Capture(string password, out byte[] data, out byte[] wal, out byte[] stale)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                using (var resource = typeof(Review3137k_Tests).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources.LegacyWal_5_0_21.zip"))
                using (var zip = new ZipArchive(resource, ZipArchiveMode.Read))
                using (var entry = zip.GetEntry(password == null ? "plain.db" : "encrypted.db").Open())
                using (var output = File.Create(file.Filename)) entry.CopyTo(output);
                using (var db = Open(file.Filename, password))
                {
                    db.GetCollection("newrows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" });
                    db.Checkpoint();
                }
                CaptureCheckpoint(file.Filename, password, 41);
                stale = File.ReadAllBytes(log);
                // Recover the older checkpoint, then capture a later failed checkpoint of this same database.
                using (var db = Open(file.Filename, password)) db.GetCollection("rows").FindAll().Count().Should().Be(1);
                CaptureCheckpoint(file.Filename, password, 42);
                data = File.ReadAllBytes(file.Filename);
                wal = File.ReadAllBytes(log);
                using var raw = new MemoryStream(wal);
                using var factory = new StreamFactory(raw, password);
                using var stream = factory.GetStream(false, false);
                var journal = HeaderJournal.Read(stream);
                journal.Should().NotBeNull();
                journal.Legacy.Should().BeFalse();
                journal.ValidateCheckpointWal(stream, journal.Header);
            }
            finally { File.Delete(log); }
        }

        private static void CaptureCheckpoint(string filename, string password, int value)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = filename, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = value });
            engine.SimulateDataWriteFail = page => throw new IOException("3137k capture");
            Action checkpoint = () => db.Checkpoint();
            checkpoint.Should().Throw<IOException>().WithMessage("3137k capture");
        }

        private static byte[] Rewrite(byte[] source, string password, Action<byte[]> change)
        {
            using var input = new MemoryStream(source);
            byte[] plain;
            using (var factory = new StreamFactory(input, password))
            using (var stream = factory.GetStream(false, false))
            using (var result = new MemoryStream()) { stream.CopyTo(result); plain = result.ToArray(); }
            change(plain);
            using var output = new MemoryStream();
            output.Write(source, 0, source.Length);
            using (var factory = new StreamFactory(output, password))
            using (var stream = factory.GetStream(true, false)) stream.Write(plain, 0, plain.Length);
            return output.ToArray();
        }
    }
}
