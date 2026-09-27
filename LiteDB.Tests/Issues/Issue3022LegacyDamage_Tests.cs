using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue3022LegacyDamage_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Damaged_order_is_corruption_and_rejection_preserves_bytes(bool migrating, bool readOnly)
        {
            using var file = DamagedFile(migrating);
            var before = File.ReadAllBytes(file.Filename);
            Action open = () => new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, ReadOnly = readOnly, LegacyIndexScan = readOnly
            }).Dispose();
            open.Should().Throw<LiteException>().Where(ex => ex.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
                .WithMessage("*rows._id*AutoRebuild*");
            File.ReadAllBytes(file.Filename).Should().Equal(before);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AutoRebuild_salvages_on_first_open_and_preserves_backup_and_new_writes(bool migrating)
        {
            using var file = DamagedFile(migrating);
            var before = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, AutoRebuild = true }))
            {
                Verify(db);
                db.GetCollection("_rebuild_errors").FindAll().Single()["exception"]["code"].AsInt32
                    .Should().Be(LiteException.INVALID_DATAFILE_STATE);
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 4, ["payload"] = "new" });
            }
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(before);
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = new LiteDatabase(file.Filename);
                Verify(db);
                db.GetCollection("rows").FindById(4)["payload"].AsString.Should().Be("new");
            }
        }

        [Fact]
        public void ReadOnly_AutoRebuild_never_repairs_or_marks_a_damaged_source()
        {
            using var file = DamagedFile(true);
            var before = File.ReadAllBytes(file.Filename);
            Action open = () => new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, ReadOnly = true, LegacyIndexScan = true, AutoRebuild = true
            }).Dispose();
            open.Should().Throw<LiteException>().Where(ex => ex.ErrorCode == LiteException.INVALID_DATAFILE_STATE);
            File.ReadAllBytes(file.Filename).Should().Equal(before);
            File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeFalse();
        }

        [Fact]
        public void Known_signed_ObjectId_order_is_not_corruption()
        {
            var high = new BsonValue(new ObjectId("800000001122338000667788"));
            var low = new BsonValue(new ObjectId("000000001122337fff667788"));
            high.CompareTo(low).Should().BePositive();
            LegacyIndexComparison.IsInvariantViolation(high, low, true).Should().BeFalse();
        }

        [Fact]
        public void Runtime_invariant_unique_duplicates_and_Guid_inversions_are_damage()
        {
            foreach (var key in new BsonValue[] { 1, 1L, 1m, 1.0, true, "same",
                Guid.Parse("00000001-89ab-cdef-8123-456789abcdef"), new byte[] { 0, 1, 255 } })
            {
                LegacyIndexComparison.IsInvariantViolation(key, key, true).Should().BeTrue();
                LegacyIndexComparison.IsInvariantViolation(key, key, false).Should().BeFalse();
            }
            var higher = new BsonValue(Guid.Parse("00000002-89ab-cdef-8123-456789abcdef"));
            var lower = new BsonValue(Guid.Parse("00000001-89ab-cdef-8123-456789abcdef"));
            LegacyIndexComparison.IsInvariantViolation(higher, lower, false).Should().BeTrue();
            LegacyIndexComparison.IsInvariantViolation(lower, higher, true).Should().BeFalse();
        }

        [Theory]
        [InlineData("before-recovery-marker")]
        [InlineData("before-log-backup")]
        [InlineData("after-log-backup")]
        [InlineData("before-source-backup")]
        [InlineData("after-source-backup")]
        [InlineData("before-temp-install")]
        [InlineData("after-temp-install")]
        public void Failed_opening_salvage_preserves_source_and_can_be_retried(string phase)
        {
            using var file = DamagedFile(true, wal: true);
            var before = File.ReadAllBytes(file.Filename);
            var originalLog = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
            var settings = new ConnectionString { Filename = file.Filename, AutoRebuild = true };
            for (var retry = 0; retry < 2; retry++)
            {
                var reached = false;
                try
                {
                    RebuildService.SimulateInstallFailure = point =>
                    {
                        if (point != phase) return;
                        reached = true;
                        throw new IOException("injected opening salvage failure");
                    };
                    Action open = () => new LiteDatabase(settings).Dispose();
                    open.Should().Throw<IOException>().WithMessage("injected opening salvage failure");
                }
                finally { RebuildService.SimulateInstallFailure = null; }
                reached.Should().BeTrue();
                File.ReadAllBytes(file.Filename).Should().Equal(before);
                File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(originalLog);
            }
            using var recovered = new LiteDatabase(settings);
            Verify(recovered);
        }

        [Fact]
        public void Shared_rebuild_admission_can_refuse_salvage_without_mutation()
        {
            using var file = DamagedFile(true);
            var before = File.ReadAllBytes(file.Filename);
            var consulted = false;
            Action open = () => new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, AutoRebuild = true,
                AutoRebuildAllowed = () => { consulted = true; return false; }
            }).Dispose();
            open.Should().Throw<LiteException>().Where(ex => ex.ErrorCode == LiteException.INVALID_DATAFILE_STATE);
            consulted.Should().BeTrue();
            File.ReadAllBytes(file.Filename).Should().Equal(before);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Malformed_BSON_in_migration_has_corruption_diagnostic_and_explicit_loss_report(bool autoRebuild)
        {
            using var file = DamagedFile(true, documentDamage: true);
            var before = File.ReadAllBytes(file.Filename);
            var settings = new ConnectionString { Filename = file.Filename, AutoRebuild = autoRebuild };
            if (!autoRebuild)
            {
                Action open = () => new LiteDatabase(settings).Dispose();
                open.Should().Throw<LiteException>().Where(ex => ex.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
                    .WithMessage("*Damaged document*rows*");
                File.ReadAllBytes(file.Filename).Should().Equal(before);
                return;
            }
            using (var db = new LiteDatabase(settings))
            {
                db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 2, 3 });
                db.GetCollection("_rebuild_errors").Count().Should().BeGreaterThan(1);
                db.GetCollection("unrelated").FindById(1)["payload"].AsString.Should().Be("preserved");
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(2);
        }

        private static void Verify(LiteDatabase db)
        {
            for (var i = 1; i <= 3; i++)
                db.GetCollection("rows").FindById(i)["payload"].AsString.Should().Be("original-" + i);
            db.GetCollection("unrelated").FindById(1)["payload"].AsString.Should().Be("preserved");
        }

        [Theory]
        [InlineData(10)]
        [InlineData(11)]
        public void Interrupted_promotion_does_not_interpret_old_LIMIT_SIZE_as_a_sort_stamp(byte version)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            IndexMigration_Tests.RewriteHeaders(file.Filename, null, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = version;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                for (var i = 0; i < 4; i++) header[EnginePragmas.P_COLLATION_STAMP + i] = 0xff;
            });
            var before = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true, LegacyIndexScan = true }))
                Assert.NotNull(db.GetCollection("rows").FindById(1));
            File.ReadAllBytes(file.Filename).Should().Equal(before);
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("rows").Count().Should().Be(2);
            }
        }

        internal static TempFile DamagedFile(bool migrating, bool documentDamage = false, bool wal = false)
        {
            var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename }))
            {
                engine.Insert("rows", Enumerable.Range(1, 3).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["payload"] = "original-" + i
                }), BsonAutoId.Int32);
                engine.Insert("unrelated", new[] { new BsonDocument { ["_id"] = 1, ["payload"] = "preserved" } }, BsonAutoId.Int32);
                if (wal)
                {
                    engine.Checkpoint();
                    engine.Pragma(Pragmas.CHECKPOINT, 0);
                }
                Func<TransactionService, bool> damage = transaction =>
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Write, "rows", false);
                    var indexer = new IndexService(snapshot, Collation.Binary, uint.MaxValue);
                    var nodes = indexer.FindAll(snapshot.CollectionPage.PK, Query.Ascending).ToArray();
                    if (documentDamage)
                    {
                        var data = new DataService(snapshot, uint.MaxValue).Read(nodes[0].DataBlock).First();
                        data[4] = 0x42; // Invalid BSON element type, with intact page structure/checksum.
                        snapshot.GetPage<DataPage>(nodes[0].DataBlock.PageID).IsDirty = true;
                        return true;
                    }
                    // Damage only index keys; independent document payloads remain salvageable.
                    for (var i = 0; i < 2; i++)
                    {
                        var segment = (BufferSlice)typeof(IndexNode).GetField("_segment", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(nodes[i]);
                        segment.WriteIndexKey(new BsonValue(2 - i), 12 + nodes[i].Levels * PageAddress.SIZE * 2);
                        nodes[i].Page.IsDirty = true;
                    }
                    return true;
                };
                typeof(LiteEngine).GetMethod("AutoTransaction", BindingFlags.NonPublic | BindingFlags.Instance)
                    .MakeGenericMethod(typeof(bool)).Invoke(engine, new object[] { damage });
            }
            IndexMigration_Tests.RewriteHeaders(file.Filename, null, header =>
            {
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
                if (migrating) header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
            });
            return file;
        }
    }
}
