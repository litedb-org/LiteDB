using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue3022OpeningSafety_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Failed_storage_close_blocks_replacement_and_retains_both_errors(bool logFailure, bool autoRebuild)
        {
            using var file = Issue3022LegacyDamage_Tests.DamagedFile(true, wal: true);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            var signals = new OpeningStorageGuard();
            var closed = new List<FileOrigin>();
            var failure = new IOException("injected storage close failure");
            var settings = new EngineSettings
            {
                Filename = file.Filename, AutoRebuild = autoRebuild, CoordinationSignals = signals,
                AfterDiskPoolClose = origin =>
                {
                    closed.Add(origin);
                    if (origin == (logFailure ? FileOrigin.Log : FileOrigin.Data)) throw failure;
                }
            };
            for (var retry = 0; retry < 2; retry++)
            {
                closed.Clear();
                Action open = () => new LiteEngine(settings).Dispose();
                var errors = open.Should().Throw<AggregateException>().Which.Flatten().InnerExceptions;
                errors.Should().Contain(failure);
                errors.OfType<LiteException>().Should().ContainSingle(e => e.ErrorCode == LiteException.INVALID_DATAFILE_STATE);
                closed.Should().Equal(FileOrigin.Data, FileOrigin.Log);
                signals.Begins.Should().Be(0);
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);
                File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeFalse();
                File.Exists(FileHelper.GetSuffixFile(logName, "-backup", false)).Should().BeFalse();
            }
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, AutoRebuild = true }))
            {
                Verify(db);
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 4, ["payload"] = "new" });
            }
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(data);
            File.ReadAllBytes(FileHelper.GetSuffixFile(logName, "-backup", false)).Should().Equal(log);
            using var reopened = new LiteDatabase(file.Filename);
            Verify(reopened);
            reopened.GetCollection("rows").FindById(4)["payload"].AsString.Should().Be("new");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_reopen_does_not_mark_candidate_invalid_or_create_another_backup(bool ioFailure)
        {
            using var file = Issue3022LegacyDamage_Tests.DamagedFile(true, wal: true);
            var original = File.ReadAllBytes(file.Filename);
            var originalLog = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));
            var signals = new OpeningStorageGuard();
            byte[] candidate = null;
            var opens = 0;
            Exception failure = ioFailure ? new IOException("injected reopen read failure") :
                new LiteException(LiteException.INVALID_DATAFILE_STATE, "injected reopen WAL validation failure");
            var settings = new EngineSettings
            {
                Filename = file.Filename, AutoRebuild = true, CoordinationSignals = signals,
                AutoRebuildAllowed = () => { signals.Depth.Should().Be(1); return true; },
                BeforeOpeningWalRestore = () =>
                {
                    signals.Depth.Should().Be(0, "replacement must release its scope before WAL recovery");
                    if (++opens != 2) return;
                    using var stream = new FileStream(file.Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var bytes = new MemoryStream();
                    stream.CopyTo(bytes);
                    candidate = bytes.ToArray();
                    throw failure;
                }
            };
            Action open = () => new LiteEngine(settings).Dispose();
            open.Should().Throw<Exception>().Which.Should().BeSameAs(failure);
            opens.Should().Be(2);
            candidate.Should().NotBeNull();
            candidate[HeaderPage.P_INVALID_DATAFILE_STATE].Should().Be(0);
            File.ReadAllBytes(file.Filename).Should().Equal(candidate);
            signals.Depth.Should().Be(0);
            var backup = FileHelper.GetSuffixFile(file.Filename, "-backup", false);
            File.ReadAllBytes(backup).Should().Equal(original);
            File.ReadAllBytes(FileHelper.GetSuffixFile(FileHelper.GetLogFile(file.Filename), "-backup", false))
                .Should().Equal(originalLog);
            settings.BeforeOpeningWalRestore = null;
            settings.AutoRebuildAllowed = () => throw new Exception("must not rebuild again");
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = new LiteDatabase(new LiteEngine(settings));
                Verify(db);
            }
            File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", true)).Should().BeFalse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ineligible_open_does_not_announce_replacement(bool readOnly)
        {
            using var file = Issue3022LegacyDamage_Tests.DamagedFile(false);
            var before = File.ReadAllBytes(file.Filename);
            var signals = new OpeningStorageGuard();
            Action open = () => new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, ReadOnly = readOnly, AutoRebuild = readOnly,
                CoordinationSignals = signals, AutoRebuildAllowed = () => throw new Exception("ineligible")
            }).Dispose();
            open.Should().Throw<LiteException>().Where(ex => ex.ErrorCode == LiteException.INVALID_DATAFILE_STATE);
            signals.Begins.Should().Be(0);
            File.ReadAllBytes(file.Filename).Should().Equal(before);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Readonly_legacy_scans_use_documents_without_eager_order_validation(bool autoRebuild)
        {
            using var file = Issue3022LegacyDamage_Tests.DamagedFile(true);
            var before = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, ReadOnly = true, LegacyIndexScan = true, AutoRebuild = autoRebuild
            })) Verify(db);
            File.ReadAllBytes(file.Filename).Should().Equal(before);
            File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeFalse();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Readonly_invalid_state_marker_never_triggers_replacement(bool autoRebuild)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "preserved" });
            IndexMigration_Tests.RewriteHeaders(file.Filename, null,
                header => header[HeaderPage.P_INVALID_DATAFILE_STATE] = 1);
            var before = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, ReadOnly = true, AutoRebuild = autoRebuild
            })) db.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be("preserved");
            File.ReadAllBytes(file.Filename).Should().Equal(before);
            File.Exists(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().BeFalse();
        }

        private static void Verify(LiteDatabase db)
        {
            for (var id = 1; id <= 3; id++)
                db.GetCollection("rows").FindById(id)["payload"].AsString.Should().Be("original-" + id);
            db.GetCollection("unrelated").FindById(1)["payload"].AsString.Should().Be("preserved");
        }
    }
}
