using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SortStreamFactory_Tests
    {
        [Fact]
        public void Disposed_factory_rejects_a_late_first_spill()
        {
            using var file = new TempFile();
            using var factory = new SortStreamFactory(file.Filename, null);
            factory.Dispose();
            Action open = () => { using var stream = factory.GetStream(true, false); };
            open.Should().Throw<ObjectDisposedException>();
            File.Exists(file.Filename).Should().BeFalse();
        }

        [Fact]
        public void Unix_scratch_is_unlinked_without_deleting_a_later_file_at_the_same_name()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var file = new TempFile();
            using var factory = new SortStreamFactory(file.Filename, null);
            using var stream = factory.GetStream(true, false);
            stream.WriteByte(42);
            File.Exists(file.Filename).Should().BeFalse();
            File.WriteAllText(file.Filename, "unrelated replacement");
            factory.Dispose();
            File.ReadAllText(file.Filename).Should().Be("unrelated replacement");
        }

        [Fact]
        public void Long_database_names_do_not_overflow_the_sort_filename_component()
        {
            var root = Path.Combine(Path.GetTempPath(), "litedb-sort-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var settings = new EngineSettings { Filename = Path.Combine(root, new string('x', 220) + ".db") };
                using var factory = settings.CreateTempFactory();
                using var stream = factory.GetStream(true, false);
                stream.WriteByte(42);
                stream.Position = 0;
                stream.ReadByte().Should().Be(42);
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
