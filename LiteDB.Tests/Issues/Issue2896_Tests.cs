using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2896_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Missing_read_only_database_has_a_contextual_LiteException_and_is_not_created(bool missingDirectory, bool encrypted)
        {
            var filename = Path.Combine(
                Path.GetTempPath(),
                "litedb-readonly-missing-" + Guid.NewGuid().ToString("N") + ".db");
            if (missingDirectory) filename = Path.Combine(filename, "missing.db");

            try
            {
                Action open = () =>
                {
                    using var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = filename,
                        ReadOnly = true,
                        Password = encrypted ? "secret" : null
                    });
                    db.GetCollectionNames().ToArray();
                };

                var failure = open.Should().Throw<LiteException>().Which;
                failure.Message.Should().Contain(Path.GetFileName(filename));
                failure.Message.Should().ContainEquivalentOf("read-only");
                failure.ErrorCode.Should().Be(LiteException.FILE_NOT_FOUND);
                failure.InnerException.Should().BeAssignableTo<IOException>();
                File.Exists(filename).Should().BeFalse();
            }
            finally
            {
                if (File.Exists(filename)) File.Delete(filename);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Empty_read_only_file_is_rejected_without_initialization(bool encrypted)
        {
            using var file = new TempFile();
            using (File.Create(file.Filename)) { }
            Action open = () =>
            {
                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, ReadOnly = true,
                    Password = encrypted ? "secret" : null
                });
                db.GetCollectionNames().ToArray();
            };
            open.Should().Throw<LiteException>().WithMessage("*read-only*");
            new FileInfo(file.Filename).Length.Should().Be(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Empty_read_only_caller_stream_is_left_open_and_unchanged(bool encrypted)
        {
            using var data = new MemoryStream();
            Action open = () =>
            {
                using var engine = new LiteEngine(new EngineSettings
                {
                    DataStream = data, ReadOnly = true, Password = encrypted ? "secret" : null
                });
            };
            open.Should().Throw<LiteException>().WithMessage("*read-only*");
            data.CanRead.Should().BeTrue();
            data.Length.Should().Be(0);
        }

        [Fact]
        public void Deletion_during_length_inspection_preserves_context()
        {
            using var file = new TempFile();
            using (File.Create(file.Filename)) { }
            using var factory = new FileStreamFactory(file.Filename, null, true, false);
            factory.BeforeReadLength = () => File.Delete(file.Filename);
            Action inspect = () => factory.GetLength();
            var error = inspect.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.FILE_NOT_FOUND);
            error.Message.Should().Contain(file.Filename).And.Contain("read-only");
            error.InnerException.Should().BeOfType<FileNotFoundException>();
        }

    }
}
