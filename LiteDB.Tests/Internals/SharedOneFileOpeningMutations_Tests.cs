#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileOpeningMutations_Tests
    {
        [OneFileTheory]
        [InlineData(null, "clean")]
        [InlineData("secret", "clean")]
        [InlineData(null, "new")]
        [InlineData("secret", "new")]
        [InlineData(null, "legacy")]
        [InlineData("secret", "legacy")]
        [InlineData(null, "data-tail")]
        [InlineData("secret", "data-tail")]
        [InlineData(null, "wal-tail")]
        [InlineData("secret", "wal-tail")]
        public void Writable_participant_validation_fences_actual_opening_mutations(string password, string image)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            if (image == "new")
            {
                File.Delete(files.Filename);
                if (File.Exists(files.Log)) File.Delete(files.Log);
            }
            if (image == "legacy")
            {
                using var data = ChecksumTestFiles.Copy(File.ReadAllBytes(files.Filename));
                using var log = new MemoryStream();
                ChecksumTestFiles.MakeLegacy(data, log, password);
                File.WriteAllBytes(files.Filename, data.ToArray());
            }
            if (image.EndsWith("tail"))
            {
                var path = image == "data-tail" ? files.Filename : files.Log;
                if (image == "wal-tail" && password != null)
                {
                    using var factory = new FileStreamFactory(path, password, false, false);
                    using var stream = factory.GetStream(true, false);
                }
                using var tail = new FileStream(path, FileMode.Append, FileAccess.Write);
                tail.Write(new byte[17], 0, 17);
            }
            var structures = 0;
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage != "structural-begin") return;
                structures++;
                using var probe = new SharedRangeLocks(SharedMemoryAuthority.PathFor(files.Filename));
                Action acquire = () => probe.EnterWrite();
                acquire.Should().Throw<IOException>().WithMessage("Shared main mutex / WRITE ownership mismatch.");
            };
            try
            {
                using var engine = new SharedEngine(files.Settings);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                if (image == "new")
                {
                    db.GetCollection("docs").Insert(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
                    db.GetCollection("docs").EnsureIndex("value");
                    db.GetCollection("sentinel").Insert(Row(42, 9));
                    db.GetCollection("sentinel").EnsureIndex("value");
                }
                if (image == "clean") structures.Should().Be(0);
                else structures.Should().BeGreaterThan(0, "reach the real constructor repair/promotion");
                Verify(db, 0);
                db.Checkpoint();
                Quiet(engine.MappedReaderTable);
            }
            finally { SharedMemoryReaderTable.PublicationStage = null; }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = password });
                Verify(cold, 0);
            }
        }

        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Torn_header_journal_repair_can_fail_twice_with_live_DMS_before_success(string password)
        {
            PromotionPowerLossScenario.Run(password, false, "promotion-before-header-write",
                tornPrefix: 59, damage: true, inspectFiles: (data, log) =>
                {
                    using var files = new SharedOneFilePublicationFixture(password);
                    using var anchor = new SharedEngine(files.Settings);
                    var table = anchor.MappedReaderTable;
                    File.WriteAllBytes(files.Filename, data);
                    File.WriteAllBytes(files.Log, log);
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        var hit = false;
                        EngineState.SimulateProcessCrash = stage =>
                        {
                            if (stage != "promotion-recovery-before-header-write") return;
                            hit = true;
                            (table.Authority.Map.Load(SharedMemoryLayout.StructuralEpoch) & 1).Should().Be(1);
                            Fenced(table);
                            throw new IOException("interrupted mapped header repair");
                        };
                        try
                        {
                            using var db = new LiteDatabase(anchor, disposeOnClose: false);
                            Action open = () => _ = db.Timeout;
                            open.Should().Throw<IOException>();
                            hit.Should().BeTrue();
                            Fenced(table);
                            File.ReadAllBytes(files.Log).Should().Equal(log);
                            files.WriteIsFree();
                        }
                        finally { EngineState.SimulateProcessCrash = null; }
                    }
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        using var db = new LiteDatabase(anchor, disposeOnClose: false);
                        PromotionPowerLossScenario.Verify(db, false, false);
                        db.Checkpoint();
                        Quiet(table);
                    }
                });
        }
    }
}
#endif
