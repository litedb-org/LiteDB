using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2823_Tests
    {
        private const string RealFixtureSha256 = "65891347e8e8c87ff2ff4ab90f47c2b87884dd22d2c80cfe36affafe171752f4";
        private const string CaseFixtureSha256 = "140394df0a41b5903542380e73cffb574861256c315abe37a9883183f8e34921";
        private const string RealIdsSha256 = "91a34051ce509410445a3c591f814df29ddfacbc0c4eac65f7f798ef3fefda03";
        private const string RealNonduplicateRowsSha256 = "8ca6437bc6156a327be6813bafd4da91c18d5c623c288662e83023cde8c131e4";

        // Public Data.db fixture from https://github.com/litedb-org/LiteDB/issues/2405.
        private const string RealFixtureGzipBase64 =
            "H4sICHwwnGUCA0RhdGEuZGIA7Vt7c9NGED/nISfkRXgkkAaipP2jTRmTkDKUKTCTEB7pxCETBygwDD2ssy0iS0aSSUKHT9Kv" +
            "1Zl+jPYb0DvpJN9JZ8exnYZ29gdRbmTtb/d29063dzFCCGU+c2RQA4uL+m7F9HT6H+ubpk/W1/SSaRF9cTH7d5Y9nUXtIjNE" +
            "Lxs+qW7YJacPAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA6C3YaXyf6vxfPLKf4/fYif8w/XljGne/ydFrpj+4z64Z1B+J" +
            "hmSNNtxs+yYAAAAAAAAAAAAAAAA9xTz/3c9/ghJ0Hq0MNB7R0WcBccUagsnMokZ7rgdtQDvIpAIz3nDlJLjy1NCHMpL3p5D2" +
            "rUFKuG753+U2sec/wmWSdwwyEI+8K3z89aM+SXQ6KVrw68ZhJBtutfUF16ngOh9cs2iA/hZ5LiJti+zr29j1UxaE8lpwHRTa" +
            "E1J7MN0piVEyrD8QGgiu45RiQBK9gLT1sE+B5C7x/JqF7YFYZFgQH2nSnguuZ2lfZY+NIS3FOih46FLsadYeSvSK+um3fbdc" +
            "/XjwKeUnTbBhmP5L+UOSlPyRDYSGuEPPKJR6t35YLtlppaHMGFc9kgjrVEJSUhp68Yyg+rwQ0XO8PaowpmLVb741PqX8eEZg" +
            "uUCNkiV1wRiJYh37uODU3WJgVujAi9y48eZpdSPljFEhRyeC/EyMFVlU8sYYT8Ywaxp5JLfPJlJVMGclZc64MEwmebsxA4tt" +
            "jbcnE72dTtJLJk8IBmaCgCUDdfumW7v1MZ01ocyMEC6xPcez6Xxi6EwlGCVjJoWsGRXaIzQT0hlUW/qx/rYsGzYYz3ajPAUu" +
            "KsaQKBkbMBhPdhf4eJhKiM7wYV8gFin6xMgT7NVd0y5H4n3CuM+i6XQgckxk2zFt/36FxqNAfJ+Ke4PCbMGul+lv2W2zKdl1" +
            "06ND5nDTtAl2tbjb4SwwLbRnKJ1syJW0IRVnfwfvb7sO+9alFntimjPMHGkOYwj9Yjq2FvvishDFS7w9mzAnOZtq8VhBPJWu" +
            "JCTOUokoCDuk5ri+p8WDdp6/sK627X4tnrDCrNXp7+xx3J+NbZ0NrlcF63WhfU5oa7zdGMCqtp5w/FGhy8ahuyo4Y463548X" +
            "xmwcRl1YQOiNZTwAAAC0i2D1HU0+59Bfwpf62R0LWWzGeWMa9MWDkLqeQX3PsFUn6A594g6d9WrE9Q91/7BG7i7sOhZ2TS/H" +
            "FoPsWSvH3imstaCbxt0FXhEs6NfvIf52qKJqpPNSSme8NuiZ0nD+raFapHSKNpRVU6RyVaHy5ycb+dyx9IbLuvfovehgdW3V" +
            "W8XhUvcFehEpvkgbqsosUvs9Uxvd1G1cpWrj9euCXmcalqtLxT0jVDAYKNhBO5GCcdromDlcGYbMmipWysqtty7LqmKlrvt6" +
            "q3goUPwOvZM6rKoaI70/cb1F4nmrNrYOveBj7lz+gR588pFwF4dVW6hxWDXu1dVmz4bgmUDnS/RS6qWqHO00cUYCDc/Qs0jD" +
            "vNirJmVrpGyWKWvc5gl/YCzdLIXso62G8o0TzMuxQLGLXHHCbFIN91bzeKsur5xglydadXnlJLt8VjkWVbV4r8bipHIsKmv1" +
            "no1FAAAAAAAA/3sMivX/APqYkev/5GpHtcHfq9VORrnaUR4M9Lj4d5AT6fwqKlWVJwq96mpY+w+NDY0Ji9gmW+GRzj9HqU7p" +
            "k0SHty3HXzdx2cXVnPTcwr2Gj0ILH5sG+WX1wPQKRWxRA0OiwqHnk2puzXEsgu0F/QPTS/ndOmFmq0hedEvCdrdXPxCX5tIR" +
            "DA+x5TWlyJt2Hh90w7BLDvyHJrEMr1MW7vs8dsumnSB5btqGs+/lditmcc+myRHT3bi2dI3+KM1S+HXdqb9lN7l0c7ldZ9Os" +
            "mn7HvdlmWVXwaRptO57Jzh46sGTDpqGlSemGpxed2vLUI7uuWS4T90mp5BG/U54g5ZPdUQ2fxIOcN7i7Gvtj1TbuE9snbrOU" +
            "euSaRjcpyb56tWY5xb1uSPgp1Ba90Q1N4zyrY+fnzSNSaAtvqQVTI7s9wWBYU+tcbBcJOxb0uvHAY2LV2iFpNdM9OPBdzN4q" +
            "qy6d67sgymOadya2CqbRcUDWcHGv7Dp122gyWeWJYeLcfcdy3Jjt64ccKsaHjks6YVwKEDAyDxf8Q4t4MgOV4UegXu4RsWnn" +
            "i7lN0/N/XX71qukLMCa7piueef16QS9SY/1o8lrDHuFJHkse9Y6NH0y/ZF86NtlYT3hiw/ZXbrSeNLl7jheSpSV1SHh/dilZ" +
            "qwlPeozz8nvMKxbtpYq90fsW3MJDnLngWKah4ntuGn6lpceWA7HrqlDduxOu1f6jMVx+cPvEYsgd80WFsEmwUoR0PbV3H1d3" +
            "SIm4hE7kq3a549Um44rfCCy23czBkW8pJ3E7JaJLm2P1rOlsHmSymqpp4suPS4kT3nHyuFihkWELH5XSJ3R5Rxc5ZBO/JVb7" +
            "b9frUnkCm86nCE2s/zX0B5Lr/xIqRZXxlXSJKv3FVVSnLilq8iMGRVT7V1Al0nZVURBLy9BI3XL76uIEjOr+1r2T/vCqq94N" +
            "nNiBeHjU/hQ9jZgnGXPiD/EkcrXtBZ9tccSmi6ftp7dLAQU1FNRfXkHd/UbRF1WWfzlbgaezQdD9zmEPti9hlwJ2KWCXAnYp" +
            "YJcCdilglwJ2Kf49ZPl3AFD4Naffx6H+7339DwAAAAAAAAAAAAAAwCnjHz+EuhYAgAAA";

        // Authored with LiteDB 4.1.4 so ordinal-distinct case variants reach the v5 upgrade path.
        private const string CaseFixtureGzipBase64 =
            "H4sICIFWpmoCA2lzc3VlMjgyMy1jYXNlLXY0LmRiAO3bQU7bQBSA4TeOA6UIBBvYetFVpNATNAuoxAZYICSWyE2GMKpj0zoo" +
            "F+k9eg8uBTO2Y09CIpmmUrv4P8WT0cTzXuzIkZ+diIiol4qSRq8X3TyYPLKPOLowU/31NLo3iY56ve1Q3NqhtKU+2OYszvV1" +
            "NssDAQAAAAAAAAAAAAAAf5e7Gx+suv/v37Kf3+l3zzt2uTOjL59ObKs6xbhrlXTmU8tgTZ/B1oMAAAAAAGykqxbNS/pOtRQl" +
            "6Jbc7jZTInnx1BVrqZz3vn6wEAF/Rr35YPabXdxlF/8zwdLeP5ItdwEtupmZ9OQizqfn8VhfZiMdekdep7is1rYvdT9cTjV0" +
            "qaYuVWJTjW2qSZVKeVODVv1waUv2bPgsnf7Mkn6cPD7EYT2jbLviH95t+l37rtZkyCZ6XGTwN1zxDQ4AAABsLqyW6h/9v/eb" +
            "l9zIlVy5s/Y7M5JjKX8Q8LaekeC7SUdyaFdwVUh/mCWJyU2W9mM5yPWPJ50OtWgpzuNXRF1duqyP+s2L+ix1VTGQwTyq24qF" +
            "iqUK9tEf96J8VvWlijVRiqpkRRT/vfyiSgEAAAAA/KdeAS5a+dkAUAAA";

        private static readonly string[] RealDuplicateIds =
        {
            ".LastTestplan",
            ".MeasPointChartDisplayLinear",
            ".MeasPointChartSettings",
            ".MeasPointChartShowRawProfile",
            ".MeasPointChartShowSelection"
        };

        private static readonly IDictionary<string, string[]> RealDuplicateValueHashes =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [".LastTestplan"] = new[] { "14b496192eeb588d0fda129b831c6ec557042735bab961991bc81a532841cbde" },
                [".MeasPointChartDisplayLinear"] = new[] { "72382c765018c5421034c220e99e6caf8fde998c20e17151ffa4ce201eab51b0" },
                [".MeasPointChartSettings"] = new[]
                {
                    "a075bc6ac168944d1821d77a0b016a52fb2b56148e5c8bf1bcc08c07368786eb",
                    "a8336a5f495330d626e075262c1fb93f62923309afa14b03e2fa494610d8974b"
                },
                [".MeasPointChartShowRawProfile"] = new[] { "a1a046b9f73232729ce7dd52482c773ca5de442675320d6f488d067af54760aa" },
                [".MeasPointChartShowSelection"] = new[] { "72382c765018c5421034c220e99e6caf8fde998c20e17151ffa4ce201eab51b0" }
            };

        private static readonly StringComparer TargetComparer =
            StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), true);

        [Fact(Timeout = 30000, Skip = "Known bug #2823")]
        public async Task Upgrade_keeps_every_distinct_id_and_reports_each_exact_duplicate()
        {
            await Task.Run(() => RunFixture(RealFixtureGzipBase64, RealFixtureSha256, file =>
            {
                string firstSnapshot;
                using (var db = OpenForUpgrade(file))
                {
                    firstSnapshot = AssertRealFixtureLedger(db);
                }

                AssertPublishedFiles(file, RealFixtureSha256);

                using (var reopened = OpenForUpgrade(file))
                {
                    AssertRealFixtureLedger(reopened).Should().Be(firstSnapshot,
                        "retrying Upgrade=true against the published v5 file must be a no-op");
                }

                AssertPublishedFiles(file, RealFixtureSha256);
            }));
        }

        [Fact(Timeout = 30000, Skip = "Known bug #2823")]
        public async Task Upgrade_deduplicates_case_variants_using_the_target_collation_and_reports_the_skipped_row()
        {
            await Task.Run(() => RunFixture(CaseFixtureGzipBase64, CaseFixtureSha256, file =>
            {
                TargetComparer.Equals("Case Twin.LastGageMode", "case twin.lastgagemode").Should().BeTrue(
                    "the independent .NET en-US ignore-case oracle defines the target key collision");
                StringComparer.Ordinal.Equals("Case Twin.LastGageMode", "case twin.lastgagemode").Should().BeFalse(
                    "both spellings are distinct source ids in the v4 fixture");

                string firstSnapshot;
                using (var db = OpenForUpgrade(file))
                {
                    firstSnapshot = AssertCaseFixtureLedger(db);
                }

                AssertPublishedFiles(file, CaseFixtureSha256);

                using (var reopened = OpenForUpgrade(file))
                {
                    AssertCaseFixtureLedger(reopened).Should().Be(firstSnapshot);
                }

                AssertPublishedFiles(file, CaseFixtureSha256);
            }));
        }

        private static LiteDatabase OpenForUpgrade(string file)
        {
            return new LiteDatabase(new ConnectionString
            {
                Filename = file,
                Upgrade = true,
                Collation = new Collation("en-US/IgnoreCase")
            });
        }

        private static string AssertRealFixtureLedger(LiteDatabase db)
        {
            var rows = db.GetCollection("ItemInfo").FindAll().ToArray();
            var errors = db.GetCollection("_rebuild_errors").FindAll().ToArray();
            var ids = rows.Select(x => x["_id"].AsString).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var nonduplicates = rows.Where(x => !RealDuplicateIds.Contains(x["_id"].AsString)).ToArray();
            var errorJson = errors.Select(x => JsonSerializer.Serialize(x)).ToArray();

            using (new AssertionScope())
            {
                rows.Should().HaveCount(26, "the v4 source has 31 rows and five duplicate occurrences");
                ids.Distinct(TargetComparer).Should().HaveCount(26, "one row must remain per target-collation id");
                HashStrings(ids).Should().Be(RealIdsSha256, "no distinct source id may be dropped or invented");
                HashRows(nonduplicates).Should().Be(RealNonduplicateRowsSha256,
                    "every nonduplicate source payload must survive byte-for-byte");
                errors.Should().HaveCount(5, "every skipped source occurrence needs its own durable ledger row");
                (rows.Length + errors.Length).Should().Be(31, "the destination and error ledger must balance the source row count");

                foreach (var duplicateId in RealDuplicateIds)
                {
                    var survivor = rows.Single(x => x["_id"].AsString == duplicateId);
                    HashStrings(new[] { survivor["Value"].AsString }).Should().BeOneOf(RealDuplicateValueHashes[duplicateId]);

                    var reports = errorJson.Where(x => x.IndexOf(duplicateId, StringComparison.Ordinal) >= 0).ToArray();
                    reports.Should().ContainSingle("each duplicate id needs one unambiguous error record");
                    if (reports.Length == 1)
                    {
                        reports[0].Should().Contain("ItemInfo");
                        reports[0].IndexOf("duplicat", StringComparison.OrdinalIgnoreCase).Should().BeGreaterThanOrEqualTo(0);
                    }
                }
            }

            return Snapshot(rows, errors);
        }

        private static string AssertCaseFixtureLedger(LiteDatabase db)
        {
            const string upperId = "Case Twin.LastGageMode";
            const string lowerId = "case twin.lastgagemode";
            var rows = db.GetCollection("CaseRows").FindAll().ToArray();
            var errors = db.GetCollection("_rebuild_errors").FindAll().ToArray();
            var collisionRow = rows.Single(x => TargetComparer.Equals(x["_id"].AsString, upperId));
            var survivorId = collisionRow["_id"].AsString;
            var skippedId = StringComparer.Ordinal.Equals(survivorId, upperId) ? lowerId : upperId;
            var expectedKind = StringComparer.Ordinal.Equals(survivorId, upperId) ? "case-collision-a" : "case-collision-b";
            var expectedSequence = StringComparer.Ordinal.Equals(survivorId, upperId) ? 101 : 202;
            var errorJson = errors.Select(x => JsonSerializer.Serialize(x)).ToArray();

            using (new AssertionScope())
            {
                rows.Should().HaveCount(3, "four source rows collapse to three ids under the requested collation");
                rows.Select(x => x["_id"].AsString).Distinct(TargetComparer).Should().HaveCount(3);
                collisionRow["kind"].AsString.Should().Be(expectedKind, "the survivor must be one of the source documents");
                collisionRow["sequence"].AsInt32.Should().Be(expectedSequence);
                rows.Single(x => x["_id"].AsString == "control-alpha")["sequence"].AsInt32.Should().Be(303);
                rows.Single(x => x["_id"].AsString == "control-omega")["sequence"].AsInt32.Should().Be(404);
                errors.Should().ContainSingle("the one skipped case-variant needs one ledger row");
                (rows.Length + errors.Length).Should().Be(4, "the destination and error ledger must balance the source row count");
                if (errorJson.Length == 1)
                {
                    errorJson[0].Should().Contain("CaseRows");
                    errorJson[0].Should().Contain(skippedId, "the ledger must identify the exact source spelling that was skipped");
                    errorJson[0].IndexOf("duplicat", StringComparison.OrdinalIgnoreCase).Should().BeGreaterThanOrEqualTo(0);
                }
            }

            return Snapshot(rows, errors);
        }

        private static string HashRows(IEnumerable<BsonDocument> rows)
        {
            return HashStrings(rows.OrderBy(x => x["_id"].AsString, StringComparer.Ordinal).Select(x =>
                $"{x["_id"].AsString.Length}:{x["_id"].AsString}|{x["Value"].AsString.Length}:{x["Value"].AsString}"));
        }

        private static string Snapshot(IEnumerable<BsonDocument> rows, IEnumerable<BsonDocument> errors)
        {
            return HashStrings(rows.OrderBy(x => x["_id"].ToString(), StringComparer.Ordinal)
                .Select(x => JsonSerializer.Serialize(x))
                .Concat(new[] { "--error-ledger--" })
                .Concat(errors.OrderBy(x => x["_id"].ToString(), StringComparer.Ordinal)
                    .Select(x => JsonSerializer.Serialize(x))));
        }

        private static string HashStrings(IEnumerable<string> values)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(string.Join("\n", values));
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string HashFile(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void AssertPublishedFiles(string file, string sourceHash)
        {
            var directory = Path.GetDirectoryName(file);
            Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal)
                .Should().Equal("data-backup.db", "data.db");
            HashFile(Path.Combine(directory, "data-backup.db")).Should().Be(sourceHash,
                "upgrade must retain an exact backup and retries must not replace it");
            Directory.GetFiles(directory, "data-temp*").Should().BeEmpty("successful upgrade must not leave temp data or log files");
        }

        private static void RunFixture(string gzipBase64, string expectedHash, Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2823-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "data.db");

            try
            {
                using (var compressed = new MemoryStream(Convert.FromBase64String(gzipBase64)))
                using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    gzip.CopyTo(output);
                }

                HashFile(file).Should().Be(expectedHash, "the embedded v4 oracle fixture must not drift");
                test(file);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
