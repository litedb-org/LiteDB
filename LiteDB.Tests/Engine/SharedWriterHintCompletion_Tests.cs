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
    public class SharedWriterHintCompletion_Tests
    {
        [MappedTheory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void Commit_or_rollback_ends_its_hint_after_engine_close(string password, bool rollback)
        {
            WithDatabase(password, (engine, db, hint) =>
            {
                db.BeginTrans().Should().BeTrue();
                hint().Should().BeFalse("BeginTrans has not performed a write");
                db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 7 });
                hint().Should().BeTrue("nested operations must not complete the outer writer request");
                if (rollback) db.Rollback().Should().BeTrue();
                else db.Commit().Should().BeTrue();
                hint().Should().BeFalse("completion clears the hint without waiting for its deadline");
                db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(rollback ? 0 : 7);
            });
        }

        [MappedFact]
        public void Failed_open_clears_its_request_and_preserves_committed_rows()
        {
            WithDatabase(null, (engine, db, hint) =>
            {
                engine.SimulateOpenEngine = () => throw new IOException("injected opening failure");
                Action write = () => db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 7 });
                write.Should().Throw<IOException>();
                hint().Should().BeFalse();
                engine.SimulateOpenEngine = null;
                db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(0);
            });
        }

        private static void WithDatabase(string password, Action<SharedEngine, LiteDatabase, Func<bool>> test)
        {
            var directory = Path.Combine(SharedMappedDirectory.Root, "litedb-hint-completion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            var passed = false;
            var expected = -1;
            try
            {
                using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
                    for (var i = 0; i < 3; i++) db.GetCollection("rows").FindById(1);
                    using var stream = new FileStream(SharedCoordinationPage.PagePath(file), FileMode.Open,
                        FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    using var map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.ReadWrite,
                        HandleInheritability.None, leaveOpen: true);
                    using var view = map.CreateViewAccessor(0, 4096, MemoryMappedFileAccess.ReadWrite);
                    test(engine, db, () => (view.ReadInt64(SharedCoordinationProtocol.WriterHintOffset) & 1) != 0);
                    expected = db.GetCollection("rows").FindById(1)["value"].AsInt32;
                }
                using (var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
                {
                    cold.GetCollection("rows").Count().Should().Be(1);
                    cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(expected);
                }
                passed = true;
            }
            finally { if (passed) Directory.Delete(directory, true); }
        }
    }
}
#endif
