using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedReaderFreeSlots_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-free-slots-" + Guid.NewGuid().ToString("N"));
        private string LeasePath => Directory.GetFiles(_directory, "*.lease").Single();

        [Fact]
        public void Churn_reuses_only_released_slots_and_preserves_every_live_version()
        {
            using var slots = SharedReaderSlots.Create(_directory);
            var readers = Enumerable.Range(0, 1024).Select(slots.Lease).ToArray();
            var expected = Enumerable.Range(0, readers.Length).ToArray();
            var random = new Random(3013);
            try
            {
                var length = new FileInfo(SharedReaderSlots.ContentPath(LeasePath)).Length;
                for (var round = 0; round < 12; round++)
                {
                    // Release an irregular set, including on a different thread, then refill.
                    var indices = Enumerable.Range(0, readers.Length).OrderBy(_ => random.Next()).Take(257).ToArray();
                    var release = new Thread(() =>
                    {
                        foreach (var index in indices)
                        {
                            readers[index].Dispose();
                            readers[index].Dispose();
                        }
                    });
                    release.Start();
                    release.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    SharedReaderSlots.ReadVersions(LeasePath).Should().BeEquivalentTo(
                        expected.Where((_, index) => !indices.Contains(index)));
                    foreach (var index in indices)
                    {
                        expected[index] = 1024 + round * 1024 + index;
                        readers[index] = slots.Lease(expected[index]);
                    }
                    SharedReaderSlots.ReadVersions(LeasePath).Should().BeEquivalentTo(expected);
                    new FileInfo(SharedReaderSlots.ContentPath(LeasePath)).Length.Should().Be(length);
                }
                // Disposal must keep every live lease available until its own release.
                slots.Dispose();
                SharedReaderSlots.ReadVersions(LeasePath).Should().BeEquivalentTo(expected);
            }
            finally { foreach (var reader in readers) reader.Dispose(); }
            Directory.GetFiles(_directory).Should().BeEmpty();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(8)]
        public void Failed_release_is_never_reused_even_when_the_write_completed(int bytes)
        {
            using var slots = SharedReaderSlots.Create(_directory);
            using var first = slots.Lease(7);
            var failed = slots.Lease(11);
            using var last = slots.Lease(19);
            slots.WriteOverride = (file, buffer) =>
            {
                file.Write(buffer, 0, bytes);
                throw new IOException("release outcome unknown");
            };
            failed.Dispose();
            var writes = new List<long>();
            slots.WriteOverride = (file, buffer) =>
            {
                writes.Add(file.Position);
                file.Write(buffer, 0, buffer.Length);
            };
            using var replacement = slots.Lease(23);
            writes.Should().NotContain(16, "the second slot's failed release cannot authorize reuse");
            writes.Should().Contain(32);
            var versions = SharedReaderSlots.ReadVersions(LeasePath);
            if (bytes == 4) versions.Should().BeNull("a torn slot prevents reclamation");
            else versions.Should().Contain(new[] { 7, 19, 23 });
            slots.WriteOverride = null;
        }

        [Fact]
        public void Capacity_rejection_and_failed_republication_preserve_the_free_slot()
        {
            using var slots = SharedReaderSlots.Create(_directory);
            var readers = Enumerable.Range(0, 65536).Select(slots.Lease).ToArray();
            try
            {
                Action full = () => slots.Lease(65536);
                full.Should().Throw<IOException>();
                readers[32768].Dispose();
                slots.WriteOverride = (_, __) => throw new IOException("publication refused");
                full.Should().Throw<IOException>();
                slots.WriteOverride = null;
                readers[32768] = slots.Lease(65536);
                var expected = Enumerable.Range(0, 65536).Where(x => x != 32768).Concat(new[] { 65536 });
                SharedReaderSlots.ReadVersions(LeasePath).Should().BeEquivalentTo(expected);
                full.Should().Throw<IOException>();
            }
            finally
            {
                slots.WriteOverride = null;
                foreach (var reader in readers) reader.Dispose();
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
