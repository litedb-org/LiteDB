using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class Review3137iMatrix_Tests
    {
        [Fact]
        public void Multifield_and_ciphertext_tear_matrix_recovers_all_committed_data()
        {
            var results = new List<string> { "password\tstate\treadonly\tcode\tdataChanged\twalChanged\trows\tnewrows" };
            var failures = new List<string>();
            foreach (var password in new[] { null, "wal-secret" })
            {
                using var file = new TempFile();
                var log = FileHelper.GetLogFile(file.Filename);
                try
                {
                    ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var data, out var wal);
                    var states = new Dictionary<string, byte[]> { ["intact"] = data };
                    for (var mask = 1; mask < 64; mask++)
                    {
                        var m = mask;
                        states["new-multi-" + mask] = ConvertedWalFiles.Rewrite(data, password, b =>
                        {
                            if ((m & 1) != 0) b[HeaderPage.P_FILE_VERSION] = 14;
                            if ((m & 2) != 0) Array.Clear(b, WalChecksum.MarkerPosition, 4);
                            if ((m & 4) != 0) b[BasePage.P_PAGE_FORMAT] = 0;
                            if ((m & 8) != 0) Array.Clear(b, BasePage.P_TRANSACTION_ID, 4);
                            if ((m & 16) != 0) Array.Clear(b, HeaderPage.P_CREATION_TIME, 4);
                            if ((m & 32) != 0) Array.Clear(b, WalChecksum.SaltPosition + 3, 7);
                        });
                    }
                    states["zero-only-markers-version-checksum"] = ConvertedWalFiles.Rewrite(data, password, b =>
                    {
                        b[HeaderPage.P_FILE_VERSION] &= 8;
                        b[BasePage.P_PAGE_FORMAT] = 0;
                        Array.Clear(b, WalChecksum.MarkerPosition, 4);
                        Array.Clear(b, BasePage.P_TRANSACTION_ID, 4);
                    });
                    if (password != null)
                    foreach (var offset in new[] { 0, 16, 32, 48, 80, 96, 112, 128, 160, 256 })
                    foreach (var length in new[] { 1, 7, 15 })
                    foreach (var suffix in new[] { true, false })
                    {
                        var damaged = (byte[])data.Clone();
                        Array.Clear(damaged, 8192 + offset + (suffix ? 16 - length : 0), length);
                        states["new-cipher-block-" + offset + "-" + length + "-" + suffix] = damaged;
                    }
                    foreach (var state in states)
                    foreach (var readOnly in new[] { true, false })
                    {
                        File.WriteAllBytes(file.Filename, state.Value); File.WriteAllBytes(log, wal);
                        var rows = ""; var extra = "";
                        var error = Record.Exception(() =>
                        {
                            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = readOnly });
                            rows = string.Join("|", db.GetCollection("rows").Find(Query.EQ("_id", 1)).Select(x => x.ToString()));
                            extra = string.Join("|", db.GetCollection("newrows").FindAll().Select(x => x.ToString()));
                            rows.Should().Be(new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString());
                            extra.Should().Be(new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" }.ToString());
                            db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should().Equal(rows);
                            db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
                            db.GetCollection("newrows").FindAll().Single()["value"].AsString.Should().Be("checkpointed-modern-only");
                        });
                        var dc = !File.ReadAllBytes(file.Filename).SequenceEqual(state.Value);
                        var wc = !File.Exists(log) || !File.ReadAllBytes(log).SequenceEqual(wal);
                        if (readOnly || error != null) { dc.Should().BeFalse(state.Key); wc.Should().BeFalse(state.Key); }
                        if (error != null) failures.Add($"{password ?? "plain"}/{state.Key}/{readOnly}: {error}");
                        results.Add(string.Join("\t", password ?? "plain", state.Key, readOnly, (error as LiteException)?.ErrorCode.ToString() ?? error?.GetType().Name ?? "OK", dc, wc, rows, extra));
                    }
                }
                finally { File.Delete(log); }
            }
            var output = Environment.GetEnvironmentVariable("REVIEW_380_OUT");
            if (output != null) File.WriteAllLines(output, results);
            results.Count.Should().Be(381);
            failures.Should().BeEmpty("all 380 same-database states have valid committed redo");
        }
    }
}
