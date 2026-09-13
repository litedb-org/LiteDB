using System.IO;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2820_Tests
    {
        [Theory]
        [InlineData(16, false)]
        [InlineData(16, true)]
        [InlineData(8192, false)]
        [InlineData(8192, true)]
        [InlineData(20009, false)]
        [InlineData(20009, true)]
        public void Opening_a_foreign_file_rejects_it_without_changing_any_byte(int length, bool readOnly)
        {
            using var file = new TempFile();
            var original = Enumerable.Repeat((byte)0x41, length).ToArray();
            original[0] = 0;
            File.WriteAllBytes(file.Filename, original);
            var failure = Record.Exception(() =>
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = readOnly });
                db.GetCollection("rows").Count();
            });
            using (new AssertionScope())
            {
                File.ReadAllBytes(file.Filename).Should().Equal(original, "format detection must not truncate or initialize an existing foreign file");
                failure.Should().BeOfType<LiteException>();
                if (failure is LiteException lite) lite.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
            }
            // Failed construction must also release the file handle.
            using var exclusive = File.Open(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        [Fact]
        public void Empty_new_files_remain_usable_as_databases()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, new byte[0]);
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 7, ["value"] = "new" });
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").FindById(7)["value"].AsString.Should().Be("new");
        }
    }
}
