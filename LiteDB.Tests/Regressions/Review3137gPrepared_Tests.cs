using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
namespace LiteDB.Tests.Regressions
{
    public class Review3137gPrepared_Tests
    {
        [Fact]
        public void Prepared_suffix_of_released_legacy_pages_records_recovery()
        {
            var results = new List<string> { "password\tsuffix\treadonly\tcode\tdataChanged\twalChanged\trows" };
            foreach (var password in new[] { null, "wal-secret" })
            {
                var name = password == null ? "plain" : "encrypted";
                var data = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", name + ".db");
                var legacyWal = ConvertedWalFiles.Plaintext(ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", name + "-log.db"), password);
                // A released writer reopening conversion records advances beyond transaction 1.
                for (var i = 0; i < legacyWal.Length; i += 8192)
                    new BufferSlice(legacyWal, i, 8192).Write(2u, BasePage.P_TRANSACTION_ID);
                var header = ConvertedWalFiles.Plaintext(data, password).Take(8192).ToArray();
                byte[] journal;
                using (var raw = new MemoryStream())
                {
                    using (var factory = new StreamFactory(raw, password))
                    using (var stream = factory.GetStream(true, false))
                    {
                        HeaderJournal.BackupLegacyHeader(stream, header);
                        HeaderJournal.Write(stream, header, conversion: true, new WalChecksum());
                    }
                    journal = ConvertedWalFiles.Plaintext(raw.ToArray(), password).Take(3 * 8192).ToArray();
                }
                foreach (var length in new[] { 0, 32, 512, 8191, 8192, 8192 + 32, legacyWal.Length })
                {
                    using var raw = new MemoryStream();
                    using (var factory = new StreamFactory(raw, password))
                    using (var stream = factory.GetStream(true, false))
                    {
                        stream.Write(journal, 0, journal.Length);
                        stream.Write(legacyWal, 0, legacyWal.Length);
                    }
                    var wal = raw.ToArray().Take((password == null ? 0 : 8192) + journal.Length + Math.Min(length, legacyWal.Length)).ToArray();
                    using var file = new TempFile(); var log = FileHelper.GetLogFile(file.Filename);
                    try
                    {
                        foreach (var readOnly in new[] { true, false })
                        {
                            File.WriteAllBytes(file.Filename, data); File.WriteAllBytes(log, wal);
                            var rows = "";
                            var error = Record.Exception(() =>
                            {
                                for (var attempt = 0; attempt < 2; attempt++)
                                {
                                    using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = readOnly, LegacyIndexScan = readOnly });
                                    var actual = string.Join("|", db.GetCollection("rows").FindAll().Select(x => x.ToString()));
                                    if (attempt != 0) actual.Should().Be(rows);
                                    rows = actual;
                                }
                            });
                            var dc = !File.ReadAllBytes(file.Filename).SequenceEqual(data);
                            var wc = !File.Exists(log) || !File.ReadAllBytes(log).SequenceEqual(wal);
                            if (readOnly || error != null) { dc.Should().BeFalse(); wc.Should().BeFalse(); }
                            results.Add(string.Join("\t", password ?? "plain", length, readOnly, (error as LiteException)?.ErrorCode.ToString() ?? error?.GetType().Name ?? "OK", dc, wc, rows));
                        }
                    }
                    finally { File.Delete(log); }
                }
            }
            File.WriteAllLines(Environment.GetEnvironmentVariable("REVIEW_PREPARED_OUT") ?? "/tmp/litedb-proto/phases/v-3137g-evidence/head-prepared.tsv", results);
            results.Count.Should().Be(29);
        }
    }
}
