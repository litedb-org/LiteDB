using System;
using System.Globalization;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: shared mode names its mutexes after the URI-escaped full path
    /// (SharedMutexNameFactory, #2709). The SHA-1 fallback that keeps the name short only runs on
    /// Windows, so on Linux/macOS a path whose escaped form exceeds the runtime's named-mutex limit
    /// throws ArgumentException from the Mutex constructor. Non-ASCII directory names reach the
    /// limit quickly (each Cyrillic letter escapes to 6 characters, each CJK character to 9).
    /// 5.0.21 always used Path.GetFullPath(filename).ToLower().Sha1() (40 characters).
    /// Verified with the LiteDB 5.0.21 package: the same path opens and writes in shared mode.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class SharedMutexNameLength_Tests
    {
        [UnixFact]
        public void Unix_names_are_hashed_only_beyond_the_runtime_limit()
        {

            // The runtime keeps at most 255 characters after "Global\"; SharedEngine adds up to
            // ".Turn.Mutex". An escaped name of 244 characters keeps its existing identity.
            var longest = "/" + new string('a', 241);
            var name = SharedMutexNameFactory.Create(longest, SharedMutexNameStrategy.Default);
            name.Should().Be(Uri.EscapeDataString(longest)).And.HaveLength(244);
            using (SharedMutexFactory.Create(name))
            using (SharedMutexFactory.Create(name + ".Turn")) { }

            var tooLong = "/" + new string('a', 242);
            var hashed = SharedMutexNameFactory.Create(tooLong, SharedMutexNameStrategy.Default);
            hashed.Should().StartWith("sha1-");
            SharedMutexNameFactory.Create(tooLong, SharedMutexNameStrategy.UriEscape).Should().Be(hashed);
            hashed.Should().Be("sha1-" + SharedMutexNameFactory.CreateUsingSha1(tooLong));
            using (SharedMutexFactory.Create(hashed))
            using (SharedMutexFactory.Create(hashed + ".Turn")) { }

            // The same boundary for non-ASCII names: 40 Cyrillic letters escape to 240 characters.
            var cyrillic = "/" + new string('д', 40);
            SharedMutexNameFactory.Create(cyrillic + "a", SharedMutexNameStrategy.Default)
                .Should().Be(Uri.EscapeDataString(cyrillic + "a")).And.HaveLength(244);
            SharedMutexNameFactory.Create(cyrillic + "aa", SharedMutexNameStrategy.Default)
                .Should().Be("sha1-" + SharedMutexNameFactory.CreateUsingSha1(cyrillic + "aa"));
        }

        [Fact]
        public void Names_within_the_limit_are_unchanged_and_every_name_is_deterministic()
        {
            var shortPath = Path.Combine(Path.GetTempPath(), "Data", "App.db");
            var expected = Uri.EscapeDataString(Path.GetFullPath(shortPath).ToLowerInvariant());
            foreach (var strategy in new[] { SharedMutexNameStrategy.Default, SharedMutexNameStrategy.UriEscape })
            {
                SharedMutexNameFactory.Create(shortPath, strategy).Should().Be(expected);
                SharedMutexNameFactory.Create(shortPath.ToUpperInvariant(), strategy).Should().Be(expected);
            }

            var sha1 = SharedMutexNameFactory.Create(shortPath, SharedMutexNameStrategy.Sha1Hash);
            sha1.Should().MatchRegex("^[0-9A-F]{40}$").And.Be(SharedMutexNameFactory.CreateUsingSha1(shortPath));

            // Beyond every platform's limit the name is the same hash on every call. The escaped
            // name (300+ characters) exceeds every mutex limit while the path itself stays far
            // below MAX_PATH, which .NET Framework's Path.GetFullPath enforces (260 characters).
            var longPath = "/" + new string('д', 50);
            Uri.EscapeDataString(Path.GetFullPath(longPath).ToLowerInvariant()).Length.Should().BeGreaterThan(300);
            SharedMutexNameFactory.Create(longPath, SharedMutexNameStrategy.Default)
                .Should().Be(SharedMutexNameFactory.Create(longPath, SharedMutexNameStrategy.Default))
                .And.Be("sha1-" + SharedMutexNameFactory.CreateUsingSha1(longPath));
        }

        [Fact]
        public void Names_do_not_depend_on_the_current_culture()
        {
            // Turkish lower-cases 'I' to a dotless 'ı'; names must use invariant casing.
            var paths = new[] { "/DATA/INDEX.db", "/" + new string('I', 120) + new string('Д', 40) };
            var expected = paths.Select(p => SharedMutexNameFactory.Create(p, SharedMutexNameStrategy.Default)).ToArray();
            expected[1].Should().StartWith("sha1-");

            var original = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "tr-TR", "az-Latn-AZ", "ru-RU", "en-US" })
                {
                    CultureInfo.CurrentCulture = new CultureInfo(culture);
                    paths.Select(p => SharedMutexNameFactory.Create(p, SharedMutexNameStrategy.Default))
                        .Should().Equal(expected, $"culture {culture}");
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void Shared_connection_opens_a_database_in_a_directory_with_a_non_ascii_name()
        {
            var root = Path.Combine(Path.GetTempPath(), "litedb-" + Guid.NewGuid().ToString("n").Substring(0, 8));
            var directory = Path.Combine(root, string.Concat(Enumerable.Repeat("данные", 8)));
            Directory.CreateDirectory(directory);
            try
            {
                var file = Path.Combine(directory, "app.db");

                using (var db = new LiteDatabase($"Filename={file};Connection=shared"))
                {
                    db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
                }

                using (var db = new LiteDatabase($"Filename={file};Connection=shared"))
                {
                    db.GetCollection("items").Count().Should().Be(1);
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
