using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class CollationFingerprintCache_Tests
    {
        private static readonly string[] Cultures =
        {
            "", "en-US", "de-DE", "tr-TR", "sv-SE", "ja-JP", "zh-CN", "fr-FR",
            "es-ES", "da-DK", "ar-SA", "he-IL"
        };

        private static readonly CompareOptions[] Options =
        {
            CompareOptions.None, CompareOptions.IgnoreCase, CompareOptions.IgnoreNonSpace,
            CompareOptions.IgnoreSymbols, CompareOptions.IgnoreWidth, CompareOptions.IgnoreKanaType,
            CompareOptions.StringSort, CompareOptions.Ordinal, CompareOptions.OrdinalIgnoreCase
        };

        [Fact]
        public void Concurrent_cache_collisions_preserve_the_original_fingerprint()
        {
            // More distinct keys than cache slots; newly constructed collations model
            // independent engine/header opens, including concurrent databases.
            var cases = Cultures.SelectMany(culture => Options.Select(options =>
            {
                var name = culture + "/" + options;
                return new { Name = name, Expected = CollationFingerprint.ComputeUncached(new Collation(name)) };
            })).ToArray();

            Parallel.For(0, cases.Length * 10, i =>
            {
                var item = cases[i % cases.Length];
                CollationFingerprint.Compute(new Collation(item.Name)).Should().Be(item.Expected);
            });
        }

        [Fact]
        public void Ordinal_fingerprint_retains_the_pre_cache_persisted_value()
        {
            // Captured from PR head f0228d301; unlike culture stamps it is portable.
            foreach (var culture in Cultures)
                CollationFingerprint.Compute(new Collation(culture + "/Ordinal")).Should().Be(2208904967u);
        }

        [Fact]
        public void Invalid_options_still_fail_after_warming_a_valid_comparer()
        {
            CollationFingerprint.Compute(new Collation("en-US/IgnoreCase"));
            var invalid = new Collation(1033, CompareOptions.Ordinal | CompareOptions.IgnoreCase);
            Action compute = () => CollationFingerprint.Compute(invalid);
            compute.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Warm_shared_connection_revalidates_a_changed_header_without_mutation(bool readOnly)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = "a", ["value"] = 42 });
                seed.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 7 });
            }
            var original = File.ReadAllBytes(file.Filename);
            var changed = (byte[])original.Clone();
            changed[EnginePragmas.P_COLLATION_STAMP] ^= 1;
            PageChecksum.Write(new BufferSlice(changed, 0, Constants.PAGE_SIZE));

            using var shared = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Connection = ConnectionType.Shared, ReadOnly = readOnly
            });
            shared.GetCollection("rows").FindById("a")["value"].AsInt32.Should().Be(42);
            WriteShared(file.Filename, changed);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    Action read = () => shared.GetCollection("rows").FindById("a");
                    read.Should().Throw<LiteException>().WithMessage("*collation*original*");
                    TempFile.ReadAllBytesShared(file.Filename).Should().Equal(changed);
                    File.Exists(FileHelper.GetLogFile(file.Filename)).Should().BeFalse();
                }
            }
            finally { WriteShared(file.Filename, original); }

            shared.GetCollection("rows").FindById("a")["value"].AsInt32.Should().Be(42);
            Assert.NotNull(shared.GetCollection("unrelated").FindById(7));
        }

        [Theory]
        [InlineData(null, "de-DE/IgnoreCase")]
        [InlineData("secret", "tr-TR/IgnoreCase")]
        [InlineData(null, "/Ordinal")]
        public void Reopened_indexes_match_runtime_comparisons_with_a_warm_cache(string password, string name)
        {
            using var file = new TempFile();
            var keys = new[] { "a", "A", "ä", "ae", "i", "I", "ı", "İ", "ss", "ß" };
            var collation = new Collation(name);
            using (var seed = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Password = password, Collation = collation
            }))
            {
                var rows = seed.GetCollection("rows");
                rows.Insert(keys.Select((key, i) => new BsonDocument { ["_id"] = i, ["key"] = key }));
                rows.EnsureIndex("key");
            }

            var before = File.ReadAllBytes(file.Filename);
            using (var shared = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Password = password, Connection = ConnectionType.Shared, ReadOnly = true
            }))
            {
                foreach (var search in keys)
                {
                    var expected = keys.Select((key, id) => new { key, id })
                        .Where(x => collation.Culture.CompareInfo.Compare(x.key, search, collation.SortOptions) == 0)
                        .Select(x => x.id);
                    var query = shared.GetCollection("rows").Query().Where(Query.EQ("key", search));
                    query.GetPlan()["index"]["name"].AsString.Should().Be("key");
                    query.ToEnumerable()
                        .Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
                }
            }
            File.ReadAllBytes(file.Filename).Should().Equal(before);
        }

        private static void WriteShared(string filename, byte[] bytes)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
    }
}
