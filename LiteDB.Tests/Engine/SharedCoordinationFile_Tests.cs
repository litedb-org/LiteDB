#if NET8_0_OR_GREATER
using System;
using System.IO;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinationFile_Tests
    {
        [Theory]
        [InlineData("created")]
        [InlineData("written")]
        [InlineData("flushed")]
        [InlineData("published")]
        public void Failed_creation_publishes_only_complete_bytes_and_removes_its_temporary_file(string failureStage)
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "control");
                var bytes = new byte[4096];
                BitConverter.GetBytes(SharedCoordinationFallback.Magic).CopyTo(bytes, 0);
                SharedCoordinationFile.CreationStage = (_, stage) =>
                {
                    if (stage == failureStage) throw new IOException("Injected creation failure");
                };
                try
                {
                    Action publish = () => SharedCoordinationFile.Publish(path, bytes);
                    publish.Should().Throw<IOException>().WithMessage("Injected creation failure");
                }
                finally { SharedCoordinationFile.CreationStage = null; }
                Directory.GetFiles(directory, ".litedb-control-*").Should().BeEmpty();
                if (failureStage == "published") File.ReadAllBytes(path).Should().Equal(bytes);
                else File.Exists(path).Should().BeFalse();
            });
        }

        [Fact]
        public void Publication_never_replaces_an_existing_unknown_destination()
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "control");
                var original = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(path, original);
                Action publish = () => SharedCoordinationFile.Publish(path, new byte[4096]);
                publish.Should().Throw<IOException>();
                File.ReadAllBytes(path).Should().Equal(original);
                Directory.GetFiles(directory, ".litedb-control-*").Should().BeEmpty();
            });
        }

        private static void WithDirectory(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-control-create-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(directory); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
