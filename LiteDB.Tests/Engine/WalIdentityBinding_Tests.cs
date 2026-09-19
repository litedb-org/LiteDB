using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;
using static LiteDB.Tests.Engine.WalIdentityCrash_Tests;

namespace LiteDB.Tests.Engine
{
    public class WalIdentityBinding_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Vector_promotion_and_checkpoint_preserve_identity_and_format(string password)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            WalIdentity original;
            using (var db = Open(data, log, password))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                db.Checkpoint();
                original = WalIdentity.Read(ReadHeader());
                var vectors = db.GetCollection("vectors");
                vectors.EnsureIndex("embedding", "$.embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
                vectors.Insert(new BsonDocument { ["_id"] = 1, ["embedding"] = new BsonVector(new[] { 1f, 2f }) });
            }
            using (var db = Open(data, log, password))
            {
                db.GetCollection("vectors").FindById(1)["embedding"].AsVector.Should().Equal(1f, 2f);
                db.Checkpoint();
            }
            var header = ReadHeader();
            header[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.VECTOR_FILE_VERSION);
            WalIdentity.Read(header).Database.Should().Be(original.Database);
            WalIdentity.Read(header).Epoch.Should().NotBe(original.Epoch);

            PageBuffer ReadHeader()
            {
                var settings = new EngineSettings { DataStream = data, Password = password };
                using var stream = settings.CreateDataFactory().GetStream(false, true);
                return WalIdentity.ReadHeader(stream);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Orphan_WAL_is_rejected_before_initializing_an_empty_data_stream(string password)
        {
            using var donorData = new MemoryStream();
            using var log = new MemoryStream();
            using (var donor = Open(donorData, log, password))
            {
                donor.CheckpointSize = 0;
                donor.GetCollection("rows").Insert(Row(1));
            }
            var before = log.ToArray();
            using var emptyData = new MemoryStream();
            Action open = () => { using var db = Open(emptyData, log, password); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            emptyData.Length.Should().Be(0);
            log.ToArray().Should().Equal(before);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void WAL_two_generations_old_is_rejected_without_modifying_files(string password)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            byte[] oldLog;
            using (var db = Open(data, log, password))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                oldLog = log.ToArray();
                db.Checkpoint();
                db.GetCollection("rows").Insert(Row(2));
                db.Checkpoint();
            }
            var before = data.ToArray();
            using var restoredLog = Copy(oldLog);
            Action open = () => { using var db = Open(data, restoredLog, password); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            data.ToArray().Should().Equal(before);
            restoredLog.ToArray().Should().Equal(oldLog);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void Failed_initial_identity_flush_cannot_publish_a_transaction(string password, bool persisted)
        {
            using var data = new CrashStream();
            using var log = new CrashStream();
            using (var db = Open(data, log, password))
            {
                data.PersistFailedFlush = persisted;
                data.FailFlushAt = data.FlushCount + 1;
                Action insert = () => db.GetCollection("rows").Insert(Row(1));
                insert.Should().Throw<IOException>();
            }
            using var recoveredData = Copy(data.Durable);
            using var recoveredLog = Copy(log.Durable);
            using var recovered = Open(recoveredData, recoveredLog, password);
            recovered.GetCollectionNames().Should().BeEmpty();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Torn_prefix_preserves_both_files_and_all_checkpointed_receipts(string password)
        {
            using var data = new CrashStream();
            using var log = new CrashStream();
            using (var db = Open(data, log, password))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                db.Checkpoint();
                log.TearNextWrite = true;
                Action insert = () => db.GetCollection("rows").Insert(Row(2));
                insert.Should().Throw<IOException>();
            }
            using var recoveredData = Copy(data.Durable);
            using var recoveredLog = Copy(log.Durable);
            Action open = () => { using var db = Open(recoveredData, recoveredLog, password); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            recoveredData.ToArray().Should().Equal(data.Durable);
            recoveredLog.ToArray().Should().Equal(log.Durable);

            // The fixture knows that no commit entered this WAL. This separate
            // oracle proves retained receipts, not permission to discard an
            // unknown damaged WAL during normal recovery.
            using var checkpointedData = Copy(data.Durable);
            using var emptyLog = new MemoryStream();
            using var checkpointed = Open(checkpointedData, emptyLog, password);
            AssertRows(checkpointed, 1);
        }
    }
}
