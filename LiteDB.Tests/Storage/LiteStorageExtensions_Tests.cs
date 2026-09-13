using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Storage
{
    public class LiteStorageExtensions_Tests
    {
        [Fact]
        public void Text_helpers_work_through_the_default_storage_interface()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;

            storage.WriteAllText("text", "notes.txt", "first", Encoding.Unicode);
            storage.AppendAllText("text", "notes.txt", " second", Encoding.Unicode);
            storage.ReadAllText("text", Encoding.Unicode).Should().Be("first second");

            storage.WriteAllText("text", "notes.txt", "x");
            storage.ReadAllText("text").Should().Be("x");
            storage.AppendAllText("new", "new.txt", "created");
            storage.ReadAllText("new").Should().Be("created");
        }

        [Fact]
        public void Binary_helpers_support_generic_file_identifiers()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.GetStorage<Guid>();
            var id = Guid.NewGuid();

            storage.WriteAllBytes(id, "data.bin", new byte[] { 0, 1, 255 });
            storage.ReadAllBytes(id).Should().Equal(0, 1, 255);

            storage.WriteAllBytes(id, "data.bin", new byte[] { 42 });
            storage.ReadAllBytes(id).Should().Equal(42);
            Action missing = () => storage.ReadAllBytes(Guid.NewGuid());
            missing.Should().Throw<FileNotFoundException>();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AppendAllText_serializes_concurrent_appends(bool explicitEncoding)
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;
            var encoding = explicitEncoding ? Encoding.Unicode : new UTF8Encoding(false);

            storage.WriteAllText("log", "log.txt", "start|", encoding);

            var tasks = Enumerable.Range(0, 12)
                .Select(index => Task.Run(() =>
                {
                    var value = $"{index}|";

                    if (explicitEncoding)
                    {
                        storage.AppendAllText("log", "ignored.txt", value, encoding);
                    }
                    else
                    {
                        storage.AppendAllText("log", "ignored.txt", value);
                    }
                }));

            await Task.WhenAll(tasks);

            var values = storage.ReadAllText("log", encoding).Split('|');
            values[0].Should().Be("start");
            values.Skip(1).Where(value => value.Length > 0)
                .Should().BeEquivalentTo(Enumerable.Range(0, 12).Select(index => index.ToString()));
        }

        [Fact]
        public void AppendAllText_preserves_existing_bytes_and_metadata()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;
            var metadata = new BsonDocument { ["owner"] = "test" };

            using (var stream = storage.OpenWrite("text", "original.txt", metadata))
            using (var writer = new StreamWriter(stream, Encoding.Unicode))
            {
                writer.Write("before");
            }

            var original = storage.ReadAllBytes("text");
            storage.AppendAllText("text", "replacement.bin", "after");

            var result = storage.ReadAllBytes("text");
            result.Take(original.Length).Should().Equal(original);
            result.Skip(original.Length).Should().Equal(new UTF8Encoding(false).GetBytes("after"));
            storage.FindById("text").Filename.Should().Be("original.txt");
            storage.FindById("text").Metadata["owner"].AsString.Should().Be("test");
        }

        [Fact]
        public void AppendAllText_writes_a_single_encoding_preamble()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;

            storage.AppendAllText("text", "unicode.txt", "first", Encoding.Unicode);
            storage.AppendAllText("text", "unicode.txt", " second", Encoding.Unicode);

            var bytes = storage.ReadAllBytes("text");
            var preamble = Encoding.Unicode.GetPreamble();
            bytes.Take(preamble.Length).Should().Equal(preamble);
            CountSequence(bytes, preamble).Should().Be(1);
            storage.ReadAllText("text", Encoding.Unicode).Should().Be("first second");
        }

        [Fact]
        public void AppendAllText_appends_across_multiple_storage_chunks()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;
            var original = Enumerable.Range(0, LiteFileStream<string>.MAX_CHUNK_SIZE * 2 + 17)
                .Select(index => (byte)(index % 251))
                .ToArray();
            var suffix = Encoding.BigEndianUnicode.GetBytes("appended");

            storage.WriteAllBytes("large", "large.bin", original);
            storage.AppendAllText("large", "ignored.txt", "appended", Encoding.BigEndianUnicode);

            var result = storage.ReadAllBytes("large");
            result.Take(original.Length).Should().Equal(original);
            result.Skip(original.Length).Should().Equal(suffix);
            storage.FindById("large").Length.Should().Be(original.Length + suffix.Length);
        }

        [Fact]
        public void AppendAllText_rolls_back_chunk_writes_when_encoding_fails()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;
            var original = Encoding.UTF8.GetBytes("committed");
            var invalidText = new string('x', LiteFileStream<string>.MAX_CHUNK_SIZE + 2048) + "\uD800";

            storage.WriteAllBytes("text", "original.txt", original);

            Action append = () => storage.AppendAllText(
                "text",
                "ignored.txt",
                invalidText,
                new UTF8Encoding(false, true));

            append.Should().Throw<EncoderFallbackException>();
            storage.ReadAllBytes("text").Should().Equal(original);
            storage.FindById("text").Filename.Should().Be("original.txt");

            storage.AppendAllText("text", "ignored.txt", "ok");
            storage.ReadAllBytes("text").Should().Equal(original.Concat(Encoding.UTF8.GetBytes("ok")));
        }

        [Fact]
        public void OpenAppend_rolls_back_when_a_stream_write_fails()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;
            var original = Encoding.UTF8.GetBytes("committed");

            storage.WriteAllBytes("text", "original.txt", original);

            using (var stream = storage.OpenAppend("text", "ignored.txt"))
            {
                var appended = new byte[LiteFileStream<string>.MAX_CHUNK_SIZE + 1];
                stream.Write(appended, 0, appended.Length);

                Action invalidWrite = () => stream.Write(new byte[1], 0, 2);
                invalidWrite.Should().Throw<ArgumentException>();
            }

            storage.ReadAllBytes("text").Should().Equal(original);
            storage.FindById("text").Filename.Should().Be("original.txt");
        }

        [Fact]
        public void OpenWrite_finalizes_metadata_after_a_stream_write_fails()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;

            storage.WriteAllBytes("text", "original.txt", Encoding.UTF8.GetBytes("committed"));

            using (var stream = storage.OpenWrite("text", "replacement.txt"))
            {
                Action invalidWrite = () => stream.Write(new byte[1], 0, 2);
                invalidWrite.Should().Throw<ArgumentException>();
            }

            storage.ReadAllBytes("text").Should().BeEmpty();
            storage.FindById("text").Filename.Should().Be("replacement.txt");
            storage.FindById("text").Length.Should().Be(0);
            storage.FindById("text").Chunks.Should().Be(0);
        }

        [Fact]
        public void OpenAppend_rejects_a_caller_owned_transaction()
        {
            using var database = new LiteDatabase(new MemoryStream());
            var storage = database.FileStorage;

            database.BeginTrans().Should().BeTrue();
            Action open = () => storage.OpenAppend("text", "text.txt");

            open.Should().Throw<InvalidOperationException>().WithMessage("*active transaction*");
            database.Rollback().Should().BeTrue();
        }

        private static int CountSequence(byte[] bytes, byte[] sequence)
        {
            return Enumerable.Range(0, bytes.Length - sequence.Length + 1)
                .Count(index => bytes.Skip(index).Take(sequence.Length).SequenceEqual(sequence));
        }
    }
}
