using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Utils;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    public class ReachabilityGap_Tests
    {
        [Fact]
        public void Disposing_shared_transaction_discards_pending_rows_and_releases_writer()
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared, SharedOneFile = false };
            var db = new LiteDatabase(settings);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            db.BeginTrans();
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            var before = Hits("maintenance:shared-dispose-during-transaction");
            db.Dispose();
            Hits("maintenance:shared-dispose-during-transaction").Should().BeGreaterThan(before);
            for (var retry = 0; retry < 2; retry++)
            {
                using var reopened = new LiteDatabase(settings);
                (reopened.GetCollection("rows").FindById(1) != null).Should().BeTrue();
                (reopened.GetCollection("rows").FindById(2) == null).Should().BeTrue();
                reopened.GetCollection("rows").Upsert(new BsonDocument { ["_id"] = 3 });
            }
        }

        [Fact]
        public void Pending_header_journal_refuses_append_without_mutating_wal()
        {
            using var file = new TempFile();
            using var raw = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            raw.Write(new byte[] { 1, 2, 3 }, 0, 3);
            var checksum = new WalChecksum { JournalBytes = HeaderJournal.Size };
            using var writer = new ChecksummedWalStream(raw, checksum);
            var before = Hits("refusal:wal-append-during-header-publication");
            Action append = () => writer.Write(new byte[PAGE_SIZE], 0, PAGE_SIZE);
            append.Should().Throw<IOException>().WithMessage("*header publication*");
            Hits("refusal:wal-append-during-header-publication").Should().BeGreaterThan(before);
            raw.Length.Should().Be(3);
            raw.Position.Should().Be(3);
            raw.Position = 0;
            raw.ReadByte().Should().Be(1);
            raw.ReadByte().Should().Be(2);
            raw.ReadByte().Should().Be(3);
        }

        [Fact]
        public void Handle_catalog_and_extension_reach_public_contract_and_preserve_rollback()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1 });
            var extension = Hits("api:LiteTransactionExtensions.BeginTransaction");
            var exists = Hits("api:ILiteTransaction.CollectionExists");
            var names = Hits("api:ILiteTransaction.GetCollectionNames");
            using (var tx = LiteTransactionExtensions.BeginTransaction(db))
            {
                tx.CollectionExists("sentinel").Should().BeTrue();
                tx.CollectionExists("missing").Should().BeFalse();
                tx.GetCollectionNames().Should().Contain("sentinel");
                tx.GetCollection("pending").Insert(new BsonDocument { ["_id"] = 2 });
                tx.Rollback();
            }
            Hits("api:LiteTransactionExtensions.BeginTransaction").Should().BeGreaterThan(extension);
            Hits("api:ILiteTransaction.CollectionExists").Should().BeGreaterThan(exists);
            Hits("api:ILiteTransaction.GetCollectionNames").Should().BeGreaterThan(names);
            db.GetCollection("pending").Count().Should().Be(0);
            db.GetCollection("sentinel").Count().Should().Be(1);
        }

        internal static long Hits(string name) => Reachability.Snapshot().TryGetValue(name, out var count) ? count : 0;
    }
}
