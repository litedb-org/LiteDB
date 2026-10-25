using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedReaderRegistrationFailure_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-slot-rollback-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string SlotsDirectory => Filename + "-readers";

        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 1)]
        [InlineData(false, 4)]
        [InlineData(false, 7)]
        [InlineData(false, 8)]
        [InlineData(true, 0)]
        [InlineData(true, 1)]
        [InlineData(true, 4)]
        [InlineData(true, 7)]
        [InlineData(true, 8)]
        public void Failed_append_preserves_all_published_versions_and_file_length(bool header, int bytes)
        {
            using var slots = SharedReaderSlots.Create(SlotsDirectory);
            using var a = slots.Lease(7);
            using var b = slots.Lease(11);
            var failure = new IOException("partial registration");
            var injected = false;
            slots.WriteOverride = (file, buffer) =>
            {
                if (!injected && (file.Position == 0) == header)
                {
                    injected = true;
                    file.Write(buffer, 0, bytes);
                    throw failure;
                }
                file.Write(buffer, 0, buffer.Length);
            };
            Action register = () => slots.Lease(19);
            register.Should().Throw<IOException>().Which.Should().BeSameAs(failure);
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEquivalentTo(new[] { 7, 11 });
            new FileInfo(Directory.GetFiles(SlotsDirectory, "*.slots").Single()).Length.Should().Be(24);
            slots.WriteOverride = null;
            using var retry = slots.Lease(23);
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEquivalentTo(new[] { 7, 11, 23 });
        }

        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(7)]
        public void Failed_reuse_clears_only_the_unpublished_free_slot(int bytes)
        {
            using var slots = SharedReaderSlots.Create(SlotsDirectory);
            using var a = slots.Lease(7);
            slots.Lease(11).Dispose();
            var injected = false;
            slots.WriteOverride = (file, buffer) =>
            {
                if (!injected)
                {
                    injected = true;
                    file.Write(buffer, 0, bytes);
                    throw new IOException("partial reuse");
                }
                file.Write(buffer, 0, buffer.Length);
            };
            Action register = () => slots.Lease(19);
            register.Should().Throw<IOException>();
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEquivalentTo(new[] { 7 });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_rollback_retains_unknown_content_and_the_original_error(bool header)
        {
            using var slots = SharedReaderSlots.Create(SlotsDirectory);
            using var a = slots.Lease(7);
            var failure = new IOException("registration");
            slots.WriteOverride = (file, buffer) =>
            {
                if ((file.Position == 0) == header)
                {
                    file.Write(buffer, 0, 1);
                    throw failure;
                }
                file.Write(buffer, 0, buffer.Length);
            };
            slots.TruncateOverride = (file, length) => throw new IOException("rollback");
            Action register = () => slots.Lease(11);
            register.Should().Throw<IOException>().Which.Should().BeSameAs(failure);
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeNull();
            slots.WriteOverride = null;
            slots.TruncateOverride = null;
            using var retry = slots.Lease(19);
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEquivalentTo(new[] { 7, 19 });
        }

        [Fact]
        public void Failed_header_restore_remains_unknown_until_a_successful_registration()
        {
            using var slots = SharedReaderSlots.Create(SlotsDirectory);
            using var a = slots.Lease(7);
            var failure = new IOException("header publication");
            var writes = 0;
            slots.WriteOverride = (file, buffer) =>
            {
                if (file.Position == 0)
                {
                    if (++writes == 1) { file.Write(buffer, 0, 1); throw failure; }
                    throw new IOException("header rollback");
                }
                file.Write(buffer, 0, buffer.Length);
            };
            Action register = () => slots.Lease(11);
            register.Should().Throw<IOException>().Which.Should().BeSameAs(failure);
            writes.Should().Be(2);
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeNull();
            slots.WriteOverride = null;
            using var retry = slots.Lease(19);
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEquivalentTo(new[] { 7, 19 });
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
