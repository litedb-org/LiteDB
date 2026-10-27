using System;
using System.IO;
using System.Runtime.InteropServices;
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
        public void Physical_file_opt_in_matches_qualification_and_refusal_preserves_storage()
        {
            var qualified = false;
            var root = Path.GetTempPath();
#if NET8_0_OR_GREATER
            qualified = (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux) &&
                (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                    System.Runtime.InteropServices.Architecture.X64 ||
                 System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                    System.Runtime.InteropServices.Architecture.Arm64));
            SharedMemoryAuthority.IsPlatformQualified.Should().Be(qualified);
            OneFileTestPlatform.Reason.Should().Be(qualified ? null :
                "ABI-2 mapped Shared is refused on this OS/architecture; native backend tests remain enabled.");
            var futexReason = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? OneFileTestPlatform.Reason
                : "This test exercises the Linux futex ABI; native wake backend tests remain enabled.";
            new LinuxFutexFactAttribute().Skip.Should().Be(futexReason);
            new LinuxFutexTheoryAttribute().Skip.Should().Be(futexReason);
            if (qualified) root = SharedMappedDirectory.Root;
#endif
            var directory = Path.Combine(root, "litedb-one-file-qualification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "test.db");
            try
            {
                using (var seed = new LiteDatabase(filename))
                {
                    seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "accepted" });
                    seed.GetCollection("docs").EnsureIndex("value");
                }
                var data = File.ReadAllBytes(filename);
                var log = FileHelper.GetLogFile(filename);
                var wal = File.Exists(log) ? File.ReadAllBytes(log) : null;
                var settings = new EngineSettings { Filename = filename, SharedOneFile = true };
                if (qualified)
                {
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        using var engine = new SharedEngine(settings);
                        using var db = new LiteDatabase(engine, disposeOnClose: false);
                        db.GetCollection("docs").FindOne(Query.EQ("value", "accepted"))["_id"].Should().Be(1);
                    }
                }
                else
                {
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        Action open = () => { using var engine = new SharedEngine(settings); };
                        open.Should().Throw<PlatformNotSupportedException>().WithMessage(
#if NET8_0_OR_GREATER
                            "ABI-2 Shared is disabled pending complete hosted platform qualification.");
#else
                            "SharedOneFile requires .NET 8 or later.");
#endif
                        File.ReadAllBytes(filename).Should().Equal(data);
                        if (wal == null) File.Exists(log).Should().BeFalse();
                        else File.ReadAllBytes(log).Should().Equal(wal);
                        File.Exists(filename + "-shm").Should().BeFalse();
                    }
                    using var cold = new LiteDatabase(filename);
                    cold.GetCollection("docs").FindOne(Query.EQ("value", "accepted"))["_id"].Should().Be(1);
                }
            }
            finally { WindowsFileRelease.DeleteDirectory(directory); }
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
