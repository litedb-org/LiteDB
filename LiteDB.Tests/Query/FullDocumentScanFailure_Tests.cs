using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class FullDocumentScanFailure_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Read_failure_after_the_probe_propagates_and_preserves_the_source(bool forUpdate)
        {
            using var file = CreateFile(out _);
            var bytes = TempFile.ReadAllBytesShared(file.Filename);
            using var stream = new FailingReadStream(bytes);
            using (var db = Open(stream))
            {
                var query = db.GetCollection("rows").Query().Where("Score >= 0");
                BorrowedQueryDiagnostics.Reset();
                using var cursor = (forUpdate ? query.ForUpdate().ToEnumerable() : query.ToEnumerable()).GetEnumerator();
                for (var i = 1; i <= 40; i++)
                {
                    cursor.MoveNext().Should().BeTrue();
                    cursor.Current["Score"].AsInt32.Should().Be(i);
                }
                BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(forUpdate ? 40 : 32);
                stream.Fail = true;
                Action read = () => { while (cursor.MoveNext()) { } };
                read.Should().Throw<IOException>().WithMessage("scan read failure");
                stream.Fail = false;
                cursor.Dispose();
                stream.ToArray().Should().Equal(bytes);
            }
            TempFile.ReadAllBytesShared(file.Filename).Should().Equal(bytes);
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Query().Where("Score >= 0").ToArray()
                .Select(d => d["Score"].AsInt32).Should().Equal(Enumerable.Range(1, 72));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Corrupt_late_data_page_is_reported_by_both_strategies_without_modification(bool forUpdate)
        {
            using var file = CreateFile(out var latePage);
            var bytes = TempFile.ReadAllBytesShared(file.Filename);
            bytes[checked((int)latePage * Constants.PAGE_SIZE + 100)] ^= 1;
            using var stream = new MemoryStream(bytes.ToArray());
            using (var db = Open(stream))
            {
                var query = db.GetCollection("rows").Query().Where("Score >= 0");
                BorrowedQueryDiagnostics.Reset();
                var visited = 0;
                Action read = () =>
                {
                    foreach (var row in forUpdate ? query.ForUpdate().ToEnumerable() : query.ToEnumerable())
                    {
                        row["Score"].AsInt32.Should().Be(++visited);
                    }
                };
                read.Should().Throw<LiteException>().Which.Message.Should().ContainEquivalentOf("checksum");
                visited.Should().BeGreaterThanOrEqualTo(32);
                BorrowedQueryDiagnostics.BorrowedPredicateExecutions.Should().Be(forUpdate ? visited + 1 : 32);
                stream.ToArray().Should().Equal(bytes);
            }
        }

        private static LiteDatabase Open(Stream stream) => new LiteDatabase(new LiteEngine(new EngineSettings
        {
            DataStream = stream, CompactStorage = CompactStorageMode.Legacy,
            CacheSize = 65536, TransactionPageLimit = 1
        }));

        private static TempFile CreateFile(out uint latePage)
        {
            var file = new TempFile();
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, CompactStorage = CompactStorageMode.Legacy });
            var documents = FullDocumentScan_Tests.Documents();
            foreach (var document in documents) document["Payload"] = new string('x', 20_000);
            var rows = db.GetCollection("rows");
            rows.InsertBulk(documents);
            db.Checkpoint();
            latePage = rows.FindById(60).RawId.PageID;
            return file;
        }

        private sealed class FailingReadStream : MemoryStream
        {
            internal bool Fail;
            internal FailingReadStream(byte[] bytes) : base(bytes.ToArray()) { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Fail) throw new IOException("scan read failure");
                return base.Read(buffer, offset, count);
            }
        }
    }
}
