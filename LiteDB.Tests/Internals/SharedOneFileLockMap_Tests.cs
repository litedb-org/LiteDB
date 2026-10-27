using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOneFileLockMap_Tests
    {
        [Fact]
        public void Admission_families_maintenance_and_all_slots_are_disjoint_from_mapped_requests()
        {
            DatabaseFileLock.Family.Should().Be(DatabaseFileLock.Admission + 1);
            DatabaseFileLock.ReadOnlyDirect.Should().Be(DatabaseFileLock.Admission + 2);
            DatabaseFileLock.MappedFamily.Should().Be(DatabaseFileLock.Admission + 3);
            var admission = new[] { DatabaseFileLock.Admission, DatabaseFileLock.Family,
                DatabaseFileLock.ReadOnlyDirect, DatabaseFileLock.MappedFamily };
            admission.Should().OnlyHaveUniqueItems();

            // Maintenance converts Admission on the database and DMS on the
            // coordination file; it does not allocate an additional native byte.
            var native = new[] { SharedMemoryLayout.Dms, SharedMemoryLayout.Write }
                .Concat(Enumerable.Range(0, 65536).Select(slot => SharedMemoryLayout.SlotLocks + slot))
                .Concat(admission).ToArray();
            native.Should().OnlyHaveUniqueItems();
            native.Min().Should().BeGreaterOrEqualTo(SharedMemoryEnvelope.MaximumSize,
                "every request byte in every supported process block is inside the mapped payload");
            (SharedMemoryLayout.SlotLocks + 65535).Should().BeLessThan(DatabasePathLock.DomainLength,
                "the entire slot domain ends before the path-identity domains");
            admission.Min().Should().BeGreaterOrEqualTo(5 * DatabasePathLock.DomainLength,
                "all four path-identity lock domains must end before admission");

            // Requests and maintenance publication use aligned, independent words.
            AssertWords(new[] { SharedMemoryLayout.StructuralEpoch, SharedMemoryLayout.RecoveryOwner,
                SharedMemoryLayout.WriterTenureEpoch, SharedMemoryLayout.WriterOwner,
                SharedMemoryLayout.WriterWake, SharedMemoryLayout.RequestSequence }, SharedMemoryLayout.Blocks);
            AssertWords(new[] { SharedMemoryLayout.RequestOffset, SharedMemoryLayout.TenureRequest,
                SharedMemoryLayout.RequestExpiry, SharedMemoryLayout.WakeOffset }, SharedMemoryLayout.ReaderOffset);
        }

        [Fact]
        public void Independent_descriptions_can_hold_and_release_every_native_domain_without_aliasing()
        {
            using var file = new TempFile();
            var offsets = new[] { DatabaseFileLock.Admission, DatabaseFileLock.Family,
                DatabaseFileLock.ReadOnlyDirect, DatabaseFileLock.MappedFamily,
                SharedMemoryLayout.Dms, SharedMemoryLayout.Write, SharedMemoryLayout.SlotLocks,
                SharedMemoryLayout.SlotLocks + 65535 };
            var owners = offsets.Select(_ => new DatabaseFileLock(file.Filename, false, true)).ToArray();
            try
            {
                using var probe = new DatabaseFileLock(file.Filename, false, false);
                for (var i = 0; i < offsets.Length; i++) owners[i].Lock(offsets[i], true);
                foreach (var offset in offsets) probe.Conflicts(offset).Should().BeTrue();
                for (var i = 0; i < offsets.Length; i++)
                {
                    owners[i].Dispose();
                    probe.Conflicts(offsets[i]).Should().BeFalse();
                    for (var j = i + 1; j < offsets.Length; j++)
                        probe.Conflicts(offsets[j]).Should().BeTrue("releasing a different domain must leave this owner intact");
                }
            }
            finally { foreach (var owner in owners) owner.Dispose(); }
        }

        private static void AssertWords(int[] offsets, int end)
        {
            var ordered = offsets.OrderBy(value => value).ToArray();
            foreach (var offset in ordered) (offset % 8).Should().Be(0);
            for (var i = 1; i < ordered.Length; i++) ordered[i].Should().BeGreaterOrEqualTo(ordered[i - 1] + 8);
            (ordered.Last() + 8).Should().BeLessOrEqualTo(end);
        }
    }
}
