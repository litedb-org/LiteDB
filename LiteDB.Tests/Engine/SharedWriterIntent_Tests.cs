#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedWriterIntent_Tests
    {
        [MappedTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Pragma_and_transaction_start_keep_the_cache_without_requesting_writer_pressure(bool transaction)
        {
            WithDatabase((file, engine, database, view) =>
            {
                engine.HasCachedSnapshot.Should().BeTrue();
                var observed = false;
                engine.CoordinationStage = stage =>
                {
                    if (stage != "opening") return;
                    observed = true;
                    (view.ReadInt64(SharedCoordinationProtocol.WriterHintOffset) & 1).Should().Be(0);
                    engine.HasCachedSnapshot.Should().BeTrue();
                };
                if (transaction)
                {
                    database.BeginTrans().Should().BeTrue();
                    database.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(0);
                    database.Rollback().Should().BeTrue();
                }
                else database.UserVersion.Should().Be(0);
                observed.Should().BeTrue();
                engine.HasCachedSnapshot.Should().BeTrue();
                (view.ReadInt64(SharedCoordinationProtocol.WriterHintOffset) & 1).Should().Be(0);
            });
        }

        [MappedTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_fresh_connection_announces_before_writable_open_and_clears_on_close(bool fail)
        {
            WithDatabase((file, peer, database, view) =>
            {
                using var writer = new SharedEngine(new EngineSettings { Filename = file });
                using var target = new LiteDatabase(writer);
                var observed = false;
                writer.CoordinationStage = stage =>
                {
                    if (stage != "opening") return;
                    observed = true;
                    (view.ReadInt64(SharedCoordinationProtocol.WriterHintOffset) & 1).Should().Be(1);
                    if (fail) throw new IOException("Injected fresh writer failure");
                };
                Action update = () => target.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 7 });
                if (fail) update.Should().Throw<IOException>().WithMessage("Injected fresh writer failure");
                else update();
                writer.CoordinationStage = null;
                observed.Should().BeTrue();
                (view.ReadInt64(SharedCoordinationProtocol.WriterHintOffset) & 1).Should().Be(0);
                database.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(fail ? 0 : 7);
            });
        }

        private static void WithDatabase(Action<string, SharedEngine, LiteDatabase, MemoryMappedViewAccessor> test)
        {
            using var file = new MappedTestFile();
            int expected;
            using (var engine = new SharedEngine(new EngineSettings { Filename = file })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
            using (var database = new LiteDatabase(engine))
            {
                database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
                for (var i = 0; i < 3; i++) database.GetCollection("rows").FindById(1);
                using var stream = new FileStream(SharedCoordinationFallback.PagePath(file), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                using var map = MemoryMappedFile.CreateFromFile(stream, null, 4096, MemoryMappedFileAccess.ReadWrite,
                    HandleInheritability.None, leaveOpen: true);
                using var view = map.CreateViewAccessor();
                test(file, engine, database, view);
                engine.CoordinationStage = null;
                expected = database.GetCollection("rows").FindById(1)["value"].AsInt32;
            }
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").Count().Should().Be(1);
            cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(expected);
        }
    }
}
#endif
