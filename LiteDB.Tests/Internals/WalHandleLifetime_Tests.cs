#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class WalHandleLifetime_Tests
    {
        [Fact]
        public void Wal_handle_preserves_exclusivity_truncation_and_disposal()
        {
            using var file = new TempFile();
            using var factory = new FileStreamFactory(file.Filename, null, false, false, isLog: true);
            Microsoft.Win32.SafeHandles.SafeFileHandle handle;
            using (var stream = (FileStream)factory.GetStream(true, false))
            {
                stream.IsAsync.Should().Be(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
                handle = stream.SafeFileHandle;
                stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
                stream.Flush();
                NativeFileSync.FlushDevice(handle, file.Filename);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Action secondWriter = () => { using var other = new FileStream(file.Filename,
                        FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); };
                    secondWriter.Should().Throw<IOException>();
                }
                using (var reader = factory.GetStream(false, false))
                {
                    reader.ReadByte().Should().Be(1);
                }
                stream.SetLength(0);
                stream.Length.Should().Be(0);
                stream.Position = 0;
                stream.WriteByte(42);
                stream.Flush();
                NativeFileSync.FlushDevice(handle, file.Filename);
            }
            handle.IsClosed.Should().BeTrue();
            File.ReadAllBytes(file.Filename).Should().Equal(42);
            using (var exclusive = new FileStream(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                exclusive.Length.Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Wal_reopens_after_checkpoint_rebuild_and_disposal(bool encrypted)
        {
            using var file = new TempFile();
            for (var cycle = 0; cycle < 4; cycle++)
            {
                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, Password = encrypted ? "handle-test" : null
                });
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("value", true);
                for (var id = 0; id < cycle; id++)
                    rows.FindOne(Query.EQ("value", id))["_id"].AsInt32.Should().Be(id);
                rows.Insert(new BsonDocument { ["_id"] = cycle, ["value"] = cycle });
                db.Checkpoint();
                db.Rebuild();
                db.GetCollection("rows").Count().Should().Be(cycle + 1);
            }
            using var exclusive = new FileStream(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
#endif
