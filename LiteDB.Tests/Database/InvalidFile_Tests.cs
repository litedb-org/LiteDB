using System;
using System.IO;
using System.Linq;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class InvalidFile_Tests
    {
        [Theory]
        [InlineData(15, false)]
        [InlineData(15, true)]
        [InlineData(8192, false)]
        [InlineData(8192, true)]
        [InlineData(16385, false)]
        [InlineData(16385, true)]
        public void Invalid_database_file_is_rejected_without_changes(int length, bool readOnly)
        {
            var filename = Path.Combine(
                Path.GetTempPath(),
                $"litedb-invalid-{Guid.NewGuid():N}.db");
            var content = Enumerable.Repeat((byte)'a', length).ToArray();

            try
            {
                File.WriteAllBytes(filename, content);

                Action open = () =>
                {
                    using var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = filename,
                        ReadOnly = readOnly
                    });
                };

                open.Should().Throw<LiteException>()
                    .Which.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                File.ReadAllBytes(filename).Should().Equal(content);
            }
            finally
            {
                File.Delete(filename);
            }
        }

        [Theory]
        [InlineData(15)]
        [InlineData(8192)]
        [InlineData(16385)]
        public void Invalid_database_stream_is_rejected_without_changes(int length)
        {
            var content = Enumerable.Repeat((byte)'a', length).ToArray();
            using var stream = new MemoryStream(content.ToArray());

            Action open = () =>
            {
                using var db = new LiteDatabase(stream);
            };

            open.Should().Throw<LiteException>()
                .Which.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
            stream.ToArray().Should().Equal(content);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Valid_database_with_incomplete_page_remains_readable(bool readOnly)
        {
            var filename = Path.Combine(
                Path.GetTempPath(),
                $"litedb-incomplete-{Guid.NewGuid():N}.db");

            try
            {
                using (var db = new LiteDatabase(filename))
                {
                    db.GetCollection("items").Insert(new BsonDocument { ["value"] = 1 });
                    db.Checkpoint();
                }

                var completeLength = new FileInfo(filename).Length;
                using (var stream = new FileStream(filename, FileMode.Append, FileAccess.Write))
                {
                    stream.Write(new byte[37], 0, 37);
                }

                using (var db = new LiteDatabase(new ConnectionString
                {
                    Filename = filename,
                    ReadOnly = readOnly
                }))
                {
                    db.GetCollection("items").Count().Should().Be(1);
                }

                new FileInfo(filename).Length.Should().Be(
                    readOnly ? completeLength + 37 : completeLength);
            }
            finally
            {
                File.Delete(filename);
            }
        }

        [Fact]
        public void Legacy_database_rejected_without_upgrade_remains_upgradeable()
        {
            // Both xunit and the published AOT host establish this resource-relative working directory.
            var source = Path.GetFullPath("../../../Resources/Issue_2494_EncryptedV4.db");
            var filename = Path.Combine(
                Path.GetTempPath(),
                $"litedb-legacy-{Guid.NewGuid():N}.db");
            var backup = Path.Combine(
                Path.GetDirectoryName(filename),
                Path.GetFileNameWithoutExtension(filename) + "-backup.db");

            try
            {
                File.Copy(source, filename);
                var content = File.ReadAllBytes(filename);

                Action open = () =>
                {
                    using var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = filename,
                        Password = "pass123"
                    });
                };

                open.Should().Throw<LiteException>();
                File.ReadAllBytes(filename).Should().Equal(content);

                using (var db = new LiteDatabase(new ConnectionString
                {
                    Filename = filename,
                    Password = "pass123",
                    Upgrade = true
                }))
                {
                    db.GetCollectionNames().Should().NotBeEmpty();
                }
            }
            finally
            {
                File.Delete(filename);
                File.Delete(backup);
            }
        }
    }
}
