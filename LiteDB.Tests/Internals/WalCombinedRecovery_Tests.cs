using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class WalCombinedRecovery_Tests
    {
        [Fact]
        public void CompleteCombinedWrite_SurvivesRepeatedInterruptedRecoveryCheckpoints()
        {
            using var source = new WalCombinedWriteScenario(true);
            source.Capture();
            var data = source.Data;
            var wal = source.After;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var file = new TempFile();
                var log = FileHelper.GetLogFile(file.Filename);
                File.WriteAllBytes(file.Filename, data);
                File.WriteAllBytes(log, wal);
                var hits = 0;
                try
                {
                    using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    WalCombinedWriteScenario.AssertDocuments(db, 2);
                    engine.CheckpointStage = stage =>
                    {
                        if (stage != "data-page") return;
                        // Model a crash with the first checkpoint page and its redo surviving.
                        data = TempFile.ReadAllBytesShared(file.Filename);
                        wal = TempFile.ReadAllBytesShared(log);
                        hits++;
                        throw new IOException("crash during recovery checkpoint");
                    };
                    Action checkpoint = () => db.Checkpoint();
                    checkpoint.Should().Throw<IOException>().WithMessage("crash during recovery checkpoint");
                    hits.Should().Be(1);
                }
                finally { File.Delete(log); }
            }
            source.Data = data;
            source.AssertRecovered(wal, 2);
        }
    }
}
