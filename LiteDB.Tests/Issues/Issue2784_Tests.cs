using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2784_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Invalid_header_with_out_of_range_creation_time_returns_format_error(bool sqliteHeader)
        {
            using var file = new TempFile();
            var original = Enumerable.Repeat((byte)0x7f, 16384).ToArray();
            original[0] = 0;
            if (sqliteHeader) Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(original, 0);
            BitConverter.GetBytes(long.MaxValue).CopyTo(original, 68);
            File.WriteAllBytes(file.Filename, original);
            var failure = Record.Exception(() =>
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollectionNames().ToArray();
            });
            using (new AssertionScope())
            {
                failure.Should().BeOfType<LiteException>();
                if (failure is LiteException lite) lite.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                File.ReadAllBytes(file.Filename).Should().Equal(original);
            }
        }
    }
}
