using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedDurability_Tests
    {
        [Theory]
        [InlineData(null, false, false)]
        [InlineData(null, false, true)]
        [InlineData(null, true, false)]
        [InlineData(null, true, true)]
        [InlineData("secret", false, false)]
        [InlineData("secret", false, true)]
        [InlineData("secret", true, false)]
        [InlineData("secret", true, true)]
        public void SharedCommit_HonorsDurabilitySettingAtConfirmation(string password, bool durable, bool explicitTransaction)
        {
            using var file = new TempFile();
            using var data = new SyncFile(file.Filename);
            using var log = new SyncFile(file.Filename + "-wal");
            using var engine = new SharedEngine(new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log,
                Password = password, DurableCommits = durable
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
            var before = -1;
            var after = -1;
            EngineState.SimulateProcessCrash = stage =>
            {
                if (stage == "wal-before-durable-flush") before = log.DurableSyncs;
                if (stage == "wal-after-durable-flush") after = log.DurableSyncs;
            };
            try
            {
                if (explicitTransaction) db.BeginTrans().Should().BeTrue();
                rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "after" }).Should().BeTrue();
                if (explicitTransaction) db.Commit().Should().BeTrue();
            }
            finally { EngineState.SimulateProcessCrash = null; }
            before.Should().BeGreaterThanOrEqualTo(0);
            after.Should().Be(before + (durable ? 1 : 0));
            IsDurable(db).Should().Be(durable);
            rows.FindById(1)["value"].AsString.Should().Be("after");
        }

        /// <summary>
        /// A shared connection's commit whose log sync answers "cannot sync" after the log was proven:
        /// with durable commits (the default) it is not acknowledged (decision 3). Its frames reached
        /// the operating system, so its error says the outcome is unknown (implementation note 4). The
        /// connection's later engines report the weaker guarantee; a new connection starts fresh and
        /// lets the files decide: the commit's frames reached them.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void UnsupportedSync_FailsTheCommitWithAnUnknownOutcomeOnASharedConnection(string password)
        {
            using var file = new TempFile();
            using var data = new SyncFile(file.Filename);
            using var log = new SyncFile(file.Filename + "-wal");
            var settings = new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log, Password = password
            };
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
            var rejection = new UnauthorizedAccessException("sync unsupported");
            EngineState.SimulateProcessCrash = stage =>
            {
                if (stage == "wal-before-durable-flush") log.Failure = rejection;
            };
            try
            {
                Action update = () => rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "after" });
                update.Should().Throw<IOException>().WithMessage(WriteFailureAssert.OutcomeUnknown + "*")
                    .Which.InnerException.Should().BeSameAs(rejection);
            }
            finally
            {
                EngineState.SimulateProcessCrash = null;
                log.Failure = null;
            }
            log.RejectedSyncs.Should().Be(1);

            // The connection's next operation opens a fresh engine; keeping the record connection-wide
            // (read-only, refusing writes, showing only what was acknowledged) is a later layer's.
            IsDurable(db).Should().BeFalse("reopening for diagnostics must retain the weaker guarantee");

            using var fresh = new LiteDatabase(new SharedEngine(settings));
            WriteFailureAssert.NoneRecorded(fresh, "the failure belongs to the connection, not the file or caller settings");
            IsDurable(fresh).Should().BeTrue("the diagnostic belongs to the connection, not the file or caller settings");
            fresh.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("after");
            fresh.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            fresh.GetCollection("rows").Count().Should().Be(2);
        }

        /// <summary>
        /// Opted out of durable commits, "cannot sync" is not a failure (proposed default A): a shared
        /// connection's commit on storage that rejects device syncs never asks for one, is acknowledged,
        /// and nothing is recorded, so the connection's later engines keep writing.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void UnsupportedSync_IsNotAFailureForASharedConnectionWithoutDurableCommits(string password)
        {
            using var file = new TempFile();
            using var data = new SyncFile(file.Filename);
            using var log = new SyncFile(file.Filename + "-wal");
            var settings = new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log, Password = password, DurableCommits = false
            };
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
            // Armed once the fresh engine opened its log: opening an encrypted log syncs its preamble.
            EngineState.SimulateProcessCrash = stage =>
            {
                if (stage == "wal-before-durable-flush") log.Failure = new UnauthorizedAccessException("sync unsupported");
            };
            try { rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "after" }).Should().BeTrue(); }
            finally
            {
                EngineState.SimulateProcessCrash = null;
                log.Failure = null;
            }
            log.RejectedSyncs.Should().Be(0, "an opted-out commit never asks for a device sync");

            WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
            IsDurable(db).Should().BeFalse();
            rows.Insert(new BsonDocument { ["_id"] = 2 });
            rows.FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
            rows.FindById(1)["value"].AsString.Should().Be("after");
        }

        private static bool IsDurable(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private sealed class SyncFile : FileStream
        {
            internal Exception Failure;
            internal int RejectedSyncs;
            internal int DurableSyncs;

            internal SyncFile(string filename)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite,
                    4096, FileOptions.DeleteOnClose) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk && Failure != null)
                {
                    RejectedSyncs++;
                    throw Failure;
                }
                base.Flush(flushToDisk);
                if (flushToDisk) DurableSyncs++;
            }
        }
    }
}
