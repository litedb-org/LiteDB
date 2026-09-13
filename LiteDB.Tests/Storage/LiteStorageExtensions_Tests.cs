using System;
using System.IO;
using System.Text;
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
    }
}
