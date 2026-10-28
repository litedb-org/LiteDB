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
    public class Review3137gMatrix_Tests
    {
        [Fact]
        public void Extended_torn_header_matrix_recovers_all_committed_data()
        {
            var results = new List<string> { "password\tstate\treadonly\tcode\tdataChanged\twalChanged\trows\tnewrows" };
            foreach (var password in new[] { null, "wal-secret" })
            {
                using var file = new TempFile();
                var log = FileHelper.GetLogFile(file.Filename);
                try
                {
                    ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var data, out var wal);
                    var states = new Dictionary<string, byte[]> { ["intact"] = data };
                    for (var mask = 1; mask < 8; mask++)
                    {
                        var m = mask;
                        states["xor-combination-" + mask] = ConvertedWalFiles.Rewrite(data, password, b =>
                        {
                            if ((m & 1) != 0) b[HeaderPage.P_CREATION_TIME] ^= 1;
                            if ((m & 2) != 0) b[WalChecksum.SaltPosition] ^= 1;
                            if ((m & 4) != 0) b[HeaderPage.P_HEADER_INFO] ^= 1;
                        });
                    }
                    for (var n = 1; n <= 8; n++)
                    {
                        var count = n;
                        states["zero-creation-prefix-" + n] = ConvertedWalFiles.Rewrite(data, password, b => Array.Clear(b, HeaderPage.P_CREATION_TIME, count));
                        states["zero-creation-suffix-" + n] = ConvertedWalFiles.Rewrite(data, password, b => Array.Clear(b, HeaderPage.P_CREATION_TIME + 8 - count, count));
                    }
                    for (var n = 512; n <= 8192; n += 512)
                    {
                        var count = n;
                        foreach (var prefix in new[] { true, false })
                        {
                            var damaged = (byte[])data.Clone();
                            var skip = password == null ? 0 : 8192;
                            Array.Clear(damaged, skip + (prefix ? 0 : count), prefix ? count : 8192 - count);
                            states["raw-512-" + (prefix ? "prefix-" : "suffix-") + n] = damaged;
                        }
                    }
                    if (password != null)
                    for (var n = 1; n <= 16; n++)
                    {
                        var skip = 8192 + HeaderPage.P_CREATION_TIME / 16 * 16;
                        var damaged = (byte[])data.Clone(); Array.Clear(damaged, skip, n);
                        states["raw-creation-block-prefix-" + n] = damaged;
                        damaged = (byte[])data.Clone(); Array.Clear(damaged, skip + 16 - n, n);
                        states["raw-creation-block-suffix-" + n] = damaged;
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
                        });
                        var dc = !File.ReadAllBytes(file.Filename).SequenceEqual(state.Value);
                        var wc = !File.Exists(log) || !File.ReadAllBytes(log).SequenceEqual(wal);
                        if (readOnly || error != null) { dc.Should().BeFalse(state.Key); wc.Should().BeFalse(state.Key); }
                        error.Should().BeNull($"same-database tear {state.Key}, password={password}, readOnly={readOnly}");
                        results.Add(string.Join("\t", password ?? "plain", state.Key, readOnly, (error as LiteException)?.ErrorCode.ToString() ?? error?.GetType().Name ?? "OK", dc, wc, rows, extra));
                    }
                }
                finally { File.Delete(log); }
            }
            var output = Environment.GetEnvironmentVariable("REVIEW_MATRIX_OUT");
            if (output != null) File.WriteAllLines(output, results);
            results.Count.Should().Be(289);
        }
    }
}
