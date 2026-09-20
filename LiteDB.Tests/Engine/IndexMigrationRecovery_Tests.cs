using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexMigrationRecovery_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void Failed_migration_wal_write_reopens_with_all_documents_and_indexes(bool confirmed, bool afterAllocation)
        {
            using var data = new MemoryStream();
            using var log = new FailingMigrationLog(confirmed);
            using (var db = new LiteDatabase(data, logStream: log))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(Enumerable.Range(1, 1500).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["key"] = 1501 - i, ["values"] = new BsonArray { i, -i }
                }));
                rows.EnsureIndex("key", true);
                rows.EnsureIndex("computed", "$.key + 1", true);
                rows.EnsureIndex("values", "$.values[0]");
                db.Checkpoint();
                db.LimitSize = data.Length;
            }
            // Model an old multikey comparer that omitted the second key. The
            // equal-length metadata edit leaves the stored nodes at one per row.
            var legacyBytes = data.ToArray();
            var oldExpression = System.Text.Encoding.UTF8.GetBytes("$.values[0]");
            for (var offset = 0; offset <= legacyBytes.Length - oldExpression.Length; offset++)
            {
                // Framework LINQ Skip enumerates the prefix on every call, making
                // a per-byte scan quadratic. Compare the fixed-size window directly.
                var match = true;
                for (var i = 0; i < oldExpression.Length && match; i++)
                    match = legacyBytes[offset + i] == oldExpression[i];
                if (!match) continue;
                data.Position = offset + oldExpression.Length - 2;
                data.WriteByte((byte)'*');
            }
            data.Position = HeaderPage.P_FILE_VERSION;
            data.WriteByte(8);
            data.Position = EnginePragmas.P_INDEX_ORDER_VERSION;
            data.WriteByte(0);
            data.Position = EnginePragmas.P_COLLATION_STAMP;
            data.Write(new byte[4], 0, 4);
            if (afterAllocation) log.MinimumPage = BitConverter.ToUInt32(data.ToArray(), HeaderPage.P_LAST_PAGE_ID) + 1;
            log.Armed = true;
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log, TransactionPageLimit = 8,
                IndexMigrationLimitSize = 64 * 1024 * 1024
            };
            Action open = () => { using var engine = new LiteEngine(settings); };
            open.Should().Throw<IOException>().WithMessage("Injected migration WAL failure");
            log.Triggered.Should().BeTrue();
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(10);
            BitConverter.ToInt64(data.ToArray(), EnginePragmas.P_LIMIT_SIZE)
                .Should().Be(BitConverter.ToInt64(legacyBytes, EnginePragmas.P_LIMIT_SIZE));
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.LimitSize.Should().Be(settings.IndexMigrationLimitSize.Value);
                var rows = db.GetCollection("rows");
                rows.Count().Should().Be(1500);
                rows.Query().OrderBy("$.key").ToArray().Select(x => x["key"].AsInt32)
                    .Should().Equal(Enumerable.Range(1, 1500));
                rows.Find("$.key + 1 = 1500").Single()["_id"].AsInt32.Should().Be(2);
                rows.Count("$.values ANY = -2").Should().Be(1);
                rows.Delete(2).Should().BeTrue();
                rows.Count("$.values ANY = -2").Should().Be(0);
                db.Checkpoint();
            }
            data.ToArray()[EnginePragmas.P_INDEX_ORDER_VERSION].Should().Be(1);
        }

        private sealed class FailingMigrationLog : MemoryStream
        {
            private readonly bool _confirmed;
            private int _pages;
            internal uint MinimumPage;
            internal bool Armed;
            internal bool Triggered;

            internal FailingMigrationLog(bool confirmed) { _confirmed = confirmed; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && count == Constants.PAGE_SIZE)
                {
                    var confirmed = buffer[offset + BasePage.P_IS_CONFIRMED] != 0;
                    if ((_confirmed && confirmed) || (!_confirmed && ++_pages >= 3 && BitConverter.ToUInt32(buffer, offset + BasePage.P_PAGE_ID) >= MinimumPage))
                    {
                        Armed = false;
                        Triggered = true;
                        // An unconfirmed partial page must be discarded. A complete
                        // confirmation written before an I/O error must replay atomically.
                        base.Write(buffer, offset, _confirmed ? count : 100);
                        throw new IOException("Injected migration WAL failure");
                    }
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
