using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedIntegrationSeam_Tests
    {
        [Fact]
        public async Task Owner_identity_survives_clone_and_thread_transfer_without_granting_legacy_thread_ownership()
        {
            var token = new TransactionOwnerToken();
            var settings = new EngineSettings { TransactionOwner = token };
            var clone = await Task.Run(() => settings.Clone());
            clone.TransactionOwner.Should().BeSameAs(token);
            new TransactionOwnerToken().Should().NotBeSameAs(token);
        }

        [Fact]
        public void Unsupported_mapped_targets_refuse_explicit_opt_in()
        {
#if !NET8_0_OR_GREATER
            System.Action open = () => { using var engine = new SharedEngine(new EngineSettings { Filename = ":memory:", SharedOneFile = true }); };
            open.Should().Throw<System.PlatformNotSupportedException>();
#else
            System.Action open = () => { using var engine = new SharedEngine(new EngineSettings { Filename = ":memory:", SharedOneFile = true }); };
            open.Should().Throw<System.NotSupportedException>();
#endif
        }

        [Fact]
        public void Lock_bytes_and_reserved_future_fields_cannot_overlap_payload()
        {
            SharedMemoryLayout.Dms.Should().Be(0x40000000);
            SharedMemoryLayout.SlotLocks.Should().BeGreaterThan(SharedMemoryLayout.Write);
            SharedMemoryLayout.CoordinatorOffset.Should().BeGreaterThan(SharedMemoryLayout.Control);
            (SharedMemoryLayout.CoordinatorOffset + SharedMemoryLayout.CoordinatorBytes)
                .Should().BeLessThan(SharedMemoryLayout.Blocks);
            SharedMemoryLayout.WakeOffset.Should().BeGreaterThan(SharedMemoryLayout.RequestOffset);
            SharedMemoryLayout.ReaderOffset.Should().BeGreaterThan(SharedMemoryLayout.WakeOffset);
        }
    }
}
