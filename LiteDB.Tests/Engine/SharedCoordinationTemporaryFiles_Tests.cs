using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinationTemporaryFiles_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Cleanup_removes_only_attributed_recognizable_orphans(bool empty)
        {
            WithDirectory(directory =>
            {
                var filename = Path.Combine(directory, "test.db");
                var destination = SharedCoordinationFallback.LivePath(filename);
                var orphan = SharedCoordinationTemporaryFiles.CreateName(destination);
                var unknown = SharedCoordinationTemporaryFiles.CreateName(destination);
                var foreign = SharedCoordinationTemporaryFiles.CreateName(destination);
                var old = Path.Combine(directory, ".ldb-0123456789abcdef");
                File.WriteAllBytes(orphan, empty ? new byte[0] : SharedCoordinationProtocol.CreateParticipation(filename));
                File.WriteAllBytes(unknown, new byte[] { 1, 2, 3 });
                var other = SharedCoordinationProtocol.CreateParticipation(Path.Combine(directory, "other.db"));
                File.WriteAllBytes(foreign, other); // Model a short-tag collision with another database.
                File.WriteAllBytes(old, new byte[0]);
                SharedCoordinationTemporaryFiles.Cleanup(destination);
                File.Exists(orphan).Should().BeFalse();
                File.ReadAllBytes(unknown).Should().Equal(new byte[] { 1, 2, 3 });
                File.ReadAllBytes(foreign).Should().Equal(other);
                File.Exists(old).Should().BeTrue("old untagged names cannot be attributed safely");
            });
        }

        [Theory]
        [InlineData("created")]
        [InlineData("written")]
        [InlineData("flushed")]
        [InlineData("publishing")]
        public void Cleanup_cannot_remove_a_live_publisher_even_at_the_rename_boundary(string boundary)
        {
            WithDirectory(directory =>
            {
                var filename = Path.Combine(directory, "test.db");
                var destination = SharedCoordinationFallback.LivePath(filename);
                var observed = false;
                SharedCoordinationFile.CreationStage = (_, stage) =>
                {
                    if (stage != boundary) return;
                    observed = true;
                    var temporary = Directory.GetFiles(directory, ".ldb-*-*");
                    temporary.Should().ContainSingle();
                    SharedCoordinationTemporaryFiles.Cleanup(destination);
                    File.Exists(temporary[0]).Should().BeTrue();
                };
                var bytes = SharedCoordinationProtocol.CreateParticipation(filename);
                try { SharedCoordinationFile.Publish(destination, bytes); }
                finally { SharedCoordinationFile.CreationStage = null; }
                observed.Should().BeTrue();
                File.ReadAllBytes(destination).Should().Equal(bytes);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Windows_sharing_violations_retry_boundedly_without_changing_publication(bool permanent)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            WithDirectory(directory =>
            {
                var filename = Path.Combine(directory, "test.db");
                var destination = SharedCoordinationFallback.DisabledPath(filename);
                var attempts = 0;
                SharedCoordinationFile.CreationStage = (_, stage) =>
                {
                    if (stage == "publishing" && (++attempts <= 2 || permanent))
                        throw new IOException("Injected sharing violation", unchecked((int)0x80070020));
                };
                var bytes = BitConverter.GetBytes(SharedCoordinationProtocol.Magic);
                try
                {
                    Action publish = () => SharedCoordinationFile.Publish(destination, bytes);
                    if (permanent) publish.Should().Throw<IOException>().WithMessage("Injected sharing violation");
                    else publish();
                }
                finally { SharedCoordinationFile.CreationStage = null; }
                attempts.Should().Be(permanent ? 5 : 3);
                File.Exists(destination).Should().Be(!permanent);
                if (!permanent) File.ReadAllBytes(destination).Should().Equal(bytes);
                Directory.GetFiles(directory, ".ldb-*-*").Should().BeEmpty();
            });
        }

        private static void WithDirectory(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-control-temp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(directory); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
