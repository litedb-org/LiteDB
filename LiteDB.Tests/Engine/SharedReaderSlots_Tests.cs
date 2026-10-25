using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedReaderSlots_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-slots-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string DirectoryName => Filename + "-readers";
        private string LeasePath => Directory.GetFiles(DirectoryName, "*.lease").Single();

        [Theory]
        [InlineData(16)]
        [InlineData(8)]
        [InlineData(0)]
        public void Whole_slot_truncation_and_empty_content_remain_unknown(int length)
        {
            using var slots = SharedReaderSlots.Create(DirectoryName);
            using var a = slots.Lease(7);
            using var b = slots.Lease(11);
            slots.WriteOverride = (file, buffer) =>
            {
                file.SetLength(length);
                throw new IOException("lost published slots");
            };
            Action register = () => slots.Lease(19);
            register.Should().Throw<IOException>();
            slots.WriteOverride = null;
            register.Should().Throw<IOException>();
            // Neither rollback nor later cleanup may turn lost live versions into free slots.
            a.Dispose();
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeNull();
            new FileInfo(Path.ChangeExtension(LeasePath, ".slots")).Length.Should().Be(length);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(7)]
        public void Partial_registration_and_release_never_hide_another_live_version(int bytes)
        {
            using var slots = SharedReaderSlots.Create(DirectoryName);
            using var first = slots.Lease(7);
            var second = slots.Lease(11);
            var registry = new SharedReaderRegistry(Filename);
            slots.WriteOverride = (file, buffer) =>
            {
                file.Write(buffer, 0, bytes);
                throw new IOException("partial slot write");
            };
            second.Dispose(); // failed release is conservative
            var versions = registry.LiveVersions();
            if (versions != null) versions.Should().Contain(7);
            Action register = () => slots.Lease(19);
            register.Should().Throw<IOException>();
            versions = registry.LiveVersions();
            if (versions != null) versions.Should().Contain(7);
            slots.WriteOverride = null;
            using var retry = slots.Lease(19);
            // The failed release may still be unknown, but it must never erase the first lease.
            versions = registry.LiveVersions();
            if (versions != null) versions.Should().Contain(new[] { 7, 19 });
        }

        [Fact]
        public void Failed_append_header_restores_the_published_readers_without_another_registration()
        {
            using var slots = SharedReaderSlots.Create(DirectoryName);
            using var first = slots.Lease(7);
            slots.WriteOverride = (file, buffer) =>
            {
                if (file.Position == 0) throw new IOException("count publication failed");
                file.Write(buffer, 0, buffer.Length);
            };
            Action register = () => slots.Lease(11);
            register.Should().Throw<IOException>();
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEquivalentTo(new[] { 7 });
            slots.WriteOverride = null;
            using var retry = slots.Lease(19);
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEquivalentTo(new[] { 7, 19 });
        }

        [Fact]
        public void Legacy_and_slot_leases_are_combined_and_unrelated_files_survive_cleanup()
        {
            using var slots = SharedReaderSlots.Create(DirectoryName);
            using var current = slots.Lease(11);
            var unrelated = Path.Combine(DirectoryName, "user.slots");
            File.WriteAllText(unrelated, "unrelated");
            using (new FileStream(Path.Combine(DirectoryName, "7-old.lease"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                new SharedReaderRegistry(Filename).LiveVersions().Should().BeEquivalentTo(new[] { 7, 11 });
            }
            current.Dispose();
            slots.Dispose();
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEmpty();
            File.ReadAllText(unrelated).Should().Be("unrelated");
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
