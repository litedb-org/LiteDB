#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedMappedReaderSlots_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-mapped-slots-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string Readers => Filename + "-readers";

        public SharedMappedReaderSlots_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public void Mapped_and_fallback_versions_are_combined_and_disposal_waits_for_readers()
        {
            using var registry = new SharedReaderRegistry(Filename);
            var first = registry.RegisterMapped(0, allowCreate: true);
            var second = registry.RegisterMapped(11, allowCreate: false);
            using var fallback = registry.Register(19);
            using var scanner = new SharedReaderRegistry(Filename);
            scanner.LiveVersions().Should().BeEquivalentTo(new[] { 0, 11, 19 });
            registry.Dispose();
            scanner.LiveVersions().Should().BeEquivalentTo(new[] { 0, 11, 19 });
            first.Dispose();
            first.Dispose();
            scanner.LiveVersions().Should().BeEquivalentTo(new[] { 11, 19 });
            second.Dispose();
            scanner.LiveVersions().Should().Equal(19);
            fallback.Dispose();
            scanner.LiveVersions().Should().BeEmpty();
        }

        [Fact]
        public void Full_table_rejects_only_the_new_lease_and_reuses_released_capacity()
        {
            using var slots = SharedMappedReaderSlots.Create(Readers);
            using var scanner = new SharedReaderRegistry(Filename);
            var leases = new List<IDisposable>();
            try
            {
                for (var i = 0; i < 511; i++) leases.Add(slots.Lease(i));
                Action overflow = () => slots.Lease(700);
                overflow.Should().Throw<IOException>();
                scanner.LiveVersions().Should().BeEquivalentTo(Enumerable.Range(0, 511));
                leases[17].Dispose();
                using var replacement = slots.Lease(700);
                scanner.LiveVersions().Should().BeEquivalentTo(Enumerable.Range(0, 511).Where(i => i != 17).Append(700));
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
            scanner.LiveVersions().Should().BeEmpty();
        }

        [Theory]
        [InlineData(0, 0L)]
        [InlineData(2, 7L)]
        public void Malformed_mapped_content_blocks_the_entire_scan(int field, long value)
        {
            using var slots = SharedMappedReaderSlots.Create(Readers);
            using var lease = slots.Lease(31);
            var path = Directory.GetFiles(Readers, "*.slots").Single();
            using var view = SharedMappedReaderView.Open(path);
            view.Store(field, value);
            using var scanner = new SharedReaderRegistry(Filename);
            scanner.LiveVersions().Should().BeNull();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4095)]
        [InlineData(4104)]
        public void Wrong_sized_live_tables_are_unknown_and_never_extended(int size)
        {
            Directory.CreateDirectory(Readers);
            var path = Path.Combine(Readers, SharedMappedReaderSlots.Prefix + Guid.NewGuid().ToString("N") + ".lease");
            var content = SharedReaderSlots.ContentPath(path);
            File.WriteAllBytes(content, new byte[size]);
            using var lease = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var scanner = new SharedReaderRegistry(Filename);
            scanner.LiveVersions().Should().BeNull();
            new FileInfo(content).Length.Should().Be(size);
        }

        [Fact]
        public void Hot_registration_cannot_create_registry_files_without_database_ownership()
        {
            using var registry = new SharedReaderRegistry(Filename);
            Action register = () => registry.RegisterMapped(7, allowCreate: false);
            register.Should().Throw<IOException>();
            Directory.Exists(Readers).Should().BeFalse();
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
#endif
