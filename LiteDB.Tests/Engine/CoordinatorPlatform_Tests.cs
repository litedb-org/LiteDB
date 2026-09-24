#if !NETFRAMEWORK
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using LiteDB.Client.Coordinated;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Platform limits the coordinator must respect on every host, exercised everywhere:
    /// Unix domain socket paths (sun_path) and 64-bit reads of a read-only mapped view
    /// on 32-bit runtimes.
    /// </summary>
    public class CoordinatorPlatform_Tests
    {
        private const string MacTemp = "/var/folders/36/tjdph2t965j8snz9_vkdnw0r0000gn/T/";

        [Fact]
        public void Unix_pipe_names_fit_a_macOS_temp_directory()
        {
            var name = CoordinatorProtocol.PipeName("/data/app/db.db", windows: false, tempPath: MacTemp);

            name.Should().StartWith("lc-").And.HaveLength(27);
            Encoding.UTF8.GetByteCount(Path.Combine(MacTemp, "CoreFxPipe_" + name))
                .Should().BeLessOrEqualTo(CoordinatorProtocol.MaxUnixSocketPath);
        }

        [Fact]
        public void Unix_pipe_names_fall_back_to_a_rooted_tmp_path_when_the_temp_path_is_too_long()
        {
            var longTemp = "/" + new string('t', 120) + "/";
            var name = CoordinatorProtocol.PipeName("/data/app/db.db", windows: false, tempPath: longTemp);

            name.Should().StartWith("/tmp/lc-");
            Encoding.UTF8.GetByteCount(name).Should().BeLessOrEqualTo(CoordinatorProtocol.MaxUnixSocketPath);
        }

        [Fact]
        public void Pipe_names_identify_one_database()
        {
            var a = CoordinatorProtocol.PipeName("/data/a.db", windows: false, tempPath: MacTemp);
            CoordinatorProtocol.PipeName("/data/a.db", windows: false, tempPath: MacTemp).Should().Be(a);
            CoordinatorProtocol.PipeName("/data/b.db", windows: false, tempPath: MacTemp).Should().NotBe(a);
            CoordinatorProtocol.PipeName(@"C:\data\a.db", windows: true, tempPath: @"C:\Temp\")
                .Should().StartWith("litedb-coord-").And.HaveLength("litedb-coord-".Length + 40);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(0xFFFFFFFFL)]
        [InlineData(0x1_0000_0000L)]
        [InlineData(0x7FFF_FFFF_FFFF_FFFFL)]
        [InlineData(-2L)]
        public void Split_loads_return_the_stored_64_bit_value(long value)
        {
            var memory = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(memory, value);
                CoordinatorStatusPage.LoadSplit(memory).Should().Be(value);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        /// <summary>
        /// On 32-bit runtimes Volatile.Read(ref long) is an interlocked compare-exchange, a
        /// write that faulted on a client's read-only status page and terminated the process
        /// (all Windows x86 CI jobs). The split path must serve every client read.
        /// </summary>
        [Fact]
        public void Client_reads_through_the_read_only_status_page_with_split_loads()
        {
            using var file = new TempFile();
            CoordinatorStatusPage.ForceSplitLoads = true;
            try
            {
                using var host = new CoordinatedEngine(file.Filename);
                using var client = new CoordinatedEngine(file.Filename);
                using var clientDb = new LiteDatabase(client, disposeOnClose: false);
                var items = clientDb.GetCollection("items");
                items.Insert(new BsonDocument { ["_id"] = 1, ["name"] = "a" });
                items.EnsureIndex("name");

                var before = client.DirectReads;
                items.FindById(1)["name"].AsString.Should().Be("a");
                client.DirectReads.Should().BeGreaterThan(before, "the read used the status page and a direct snapshot");

                using var page = CoordinatorStatusPage.TryOpen(file.Filename);
                page.TryRead(out var status).Should().BeTrue();
                status.Instance.Should().NotBe(0);
            }
            finally { CoordinatorStatusPage.ForceSplitLoads = false; }
        }
    }
}
#endif
