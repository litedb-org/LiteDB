#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinationAbiRegression_Tests
    {
        [Fact]
        public void A_live_mapping_rejects_an_unsupported_protocol_header()
        {
            WithFile(file =>
            {
                using (var page = SharedCoordinationPage.Open(file))
                using (var stream = new FileStream(SharedCoordinationPage.PagePath(file), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.ReadWrite))
                using (var map = MemoryMappedFile.CreateFromFile(stream, null, 4096,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true))
                using (var view = map.CreateViewAccessor())
                {
                    page.Opened(7);
                    page.TryRead(out _).Should().BeTrue();
                    view.Write(8, 2L); // ABI version, independent of package and transaction versions.
                    page.TryRead(out _).Should().BeFalse();
                    Action publish = () => page.Committed(8);
                    publish.Should().Throw<IOException>();
                    Action attach = () => SharedCoordinationPage.Open(file).Dispose();
                    attach.Should().Throw<IOException>();
                }
            });
        }

        [Fact]
        public void An_unsupported_protocol_is_preserved_after_the_last_participant_closes()
        {
            WithFile(file =>
            {
                using (var page = SharedCoordinationPage.Open(file)) page.Opened(7);
                var path = SharedCoordinationPage.PagePath(file);
                var bytes = File.ReadAllBytes(path);
                BitConverter.GetBytes(2L).CopyTo(bytes, 8);
                File.WriteAllBytes(path, bytes);
                SharedCoordinationPage.TryRetire(file);
                File.Exists(path).Should().BeTrue();
                File.ReadAllBytes(path).Should().Equal(bytes);
            });
        }

        private static void WithFile(Action<string> action)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-abi-regression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { action(Path.Combine(directory, "test.db")); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
