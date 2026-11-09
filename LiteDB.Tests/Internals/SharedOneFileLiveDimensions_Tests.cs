#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileLiveDimensions_Tests
    {
        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public void Local_live_stream_or_writer_keeps_declared_dimensions_for_larger_and_smaller_requests(bool writer, bool larger)
        {
            using var files = new SharedOneFilePublicationFixture();
            var settings = files.Settings;
            settings.SharedProcessCapacity = 3; settings.SharedGenerationCapacity = 3;
            using var first = new SharedEngine(settings);
            using var db = new LiteDatabase(first, disposeOnClose: false);
            using var anchor = first.Query("docs", new Query());
            anchor.Read().Should().BeTrue();
            if (writer) { db.BeginTrans(); db.GetCollection("docs").Update(Row(1, 9)); }
            var table = first.MappedReaderTable;
            var header = table.Authority.Header;
            var requested = files.Settings;
            requested.SharedProcessCapacity = larger ? 8 : 1;
            requested.SharedGenerationCapacity = larger ? 512 : 1;
            requested.SharedMemorySize = larger ? 4 * 1024 * 1024 : 20480;
            using (var second = new SharedEngine(requested))
            {
                second.MappedReaderTable.Should().BeSameAs(table);
                new FileInfo(files.Filename + "-shm").Length.Should().Be(header.Size);
                table.Authority.Header.Bytes.Should().Equal(header.Bytes);
                table.HasReaders.Should().BeTrue();
            }
            if (writer) db.Rollback();
            var count = 1;
            while (anchor.Read()) { anchor.Current.Should().BeEquivalentTo(Row(++count, 0)); }
            count.Should().Be(160);
            anchor.Dispose(); first.Dispose();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
        }

        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public async Task Remote_live_stream_or_writer_is_never_resized_while_a_real_facade_attaches(bool writer, bool larger)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var remote = new MvccProcess("one-file-dimensions-hold", files.Filename, null, writer ? "writer" : "reader");
            await remote.Expect("ready");
            var bytes = File.ReadAllBytes(files.Filename + "-shm").Take(256).ToArray();
            var length = new FileInfo(files.Filename + "-shm").Length;
            using var attached = new ManualResetEventSlim();
            var pending = Task.Run(() =>
            {
                SharedMemoryAuthority.BootstrapStage = stage => { if (stage == "before-storage") attached.Set(); };
                var requested = files.Settings;
                requested.SharedProcessCapacity = larger ? 8 : 1;
                requested.SharedGenerationCapacity = larger ? 512 : 1;
                requested.SharedMemorySize = larger ? 4 * 1024 * 1024 : 20480;
                try { return new SharedEngine(requested); }
                finally { SharedMemoryAuthority.BootstrapStage = null; }
            });
            try
            {
                attached.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                File.ReadAllBytes(files.Filename + "-shm").Take(256).Should().Equal(bytes);
                new FileInfo(files.Filename + "-shm").Length.Should().Be(length);
            }
            finally { remote.Send("close"); await remote.Expect("done"); await remote.Finish(); }
            using (var engine = await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                engine.MappedReaderTable.Authority.Header.Bytes.Should().Equal(bytes);
                using var db = new LiteDatabase(engine, disposeOnClose: false); Verify(db, 0);
            }
        }

        [OneFileFact]
        public void Saturated_live_generations_release_failed_admission_and_rollback_then_recover_capacity()
        {
            using var files = new SharedOneFilePublicationFixture();
            var settings = files.Settings; settings.SharedGenerationCapacity = 3;
            using var reader = new SharedEngine(settings);
            using var writer = new SharedEngine(settings);
            using var first = reader.Query("docs", new Query()); first.Read().Should().BeTrue();
            writer.Update("docs", new[] { Row(1, 1) });
            using var second = reader.Query("docs", new Query()); second.Read().Should().BeTrue();
            writer.Update("docs", new[] { Row(1, 2) });
            using var third = reader.Query("docs", new Query()); third.Read().Should().BeTrue();
            writer.Update("docs", new[] { Row(1, 3) });
            Action saturated = () => { using var result = reader.Query("docs", new Query()); };
            saturated.Should().Throw<SharedGenerationCapacityException>();
            files.WriteIsFree();
            writer.BeginTrans().Should().BeTrue(); writer.Update("docs", new[] { Row(1, 99) });
            writer.Rollback().Should().BeTrue();
            first.Dispose();
            using (var recovered = new LiteDatabase(reader, disposeOnClose: false)) Verify(recovered, 3);
            var count = 1;
            second.Current.Should().BeEquivalentTo(Row(1, 1));
            while (second.Read()) { second.Current.Should().BeEquivalentTo(Row(++count, 0)); }
            count.Should().Be(160);
            count = 1; third.Current.Should().BeEquivalentTo(Row(1, 2));
            while (third.Read()) { third.Current.Should().BeEquivalentTo(Row(++count, 0)); }
            count.Should().Be(160);
            second.Dispose(); third.Dispose(); reader.Dispose(); writer.Dispose();
            for (var repeat = 0; repeat < 2; repeat++) { using var cold = new LiteDatabase(files.Filename); Verify(cold, 3); }
        }

        [OneFileTheory]
        [InlineData("stride")] [InlineData("size")]
        public void Checksummed_impossible_dimensions_refuse_without_storage_mutation(string field)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings)) { }
            var map = File.ReadAllBytes(files.Filename + "-shm");
            var header = map.Take(256).ToArray();
            Buffer.BlockCopy(BitConverter.GetBytes(field == "stride" ? 1 : 4096), 0, header, field == "stride" ? 32 : 16, 4);
            Buffer.BlockCopy(SharedMemoryEnvelope.Hash(header.Take(224).ToArray()), 0, header, 224, 32);
            Buffer.BlockCopy(header, 0, map, 0, 256);
            Buffer.BlockCopy(header, 0, map, SharedMemoryLayout.Page, 256);
            File.WriteAllBytes(files.Filename + "-shm", map);
            var data = File.ReadAllBytes(files.Filename);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                Action attach = () => { using var engine = new SharedEngine(files.Settings); };
                attach.Should().Throw<IOException>();
                File.ReadAllBytes(files.Filename).Should().Equal(data);
                File.ReadAllBytes(files.Filename + "-shm").Should().Equal(map);
            }
        }
    }
}
#endif
