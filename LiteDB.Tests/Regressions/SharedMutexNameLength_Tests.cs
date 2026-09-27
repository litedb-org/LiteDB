using System;
using System.IO;
using System.Linq;
using FluentAssertions;
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
