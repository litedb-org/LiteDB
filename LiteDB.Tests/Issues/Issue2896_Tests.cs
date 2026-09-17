using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2896_Tests
    {
        [Fact]
        [Trait("Category", "PendingBug")]
        public void Missing_read_only_database_has_a_contextual_LiteException_and_is_not_created()
        {
            var filename = Path.Combine(
                Path.GetTempPath(),
                "litedb-readonly-missing-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                Action open = () =>
                {
                    using var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = filename,
                        ReadOnly = true
                    });
                    db.GetCollectionNames().ToArray();
                };

                var failure = open.Should().Throw<LiteException>().Which;
                failure.Message.Should().Contain(Path.GetFileName(filename));
                failure.Message.Should().ContainEquivalentOf("read-only");
                File.Exists(filename).Should().BeFalse();
            }
            finally
            {
                if (File.Exists(filename)) File.Delete(filename);
            }
        }
    }
}
