using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using FluentAssertions;

namespace LiteDB.Tests.Issues
{
    internal sealed partial class Issue2043_BulkLifecycleScenario
    {
        private BsonDocument Row(int sequence)
        {
            var row = new BsonDocument
            {
                ["seq"] = sequence,
                ["bucket"] = sequence % 7,
                ["payload"] = Payload(sequence)
            };
            if (sequence == 0) row["_id"] = 31000L;
            return row;
        }

        private string Payload(int sequence) => sequence + ":" +
            new string((char)('A' + sequence % 26), _payloadBytes + sequence % 3 * 257);

        private void InsertBatch(int start, int count)
        {
            var records = _database.GetCollection("records", BsonAutoId.Int64);
            var rows = Enumerable.Range(start, count).Select(Row).ToArray();
            records.Insert(rows).Should().Be(count, "the reported writer uses one bulk Insert call");
            for (var offset = 0; offset < rows.Length; offset++)
            {
                VerifyRow(rows[offset], start + offset);
                VerifyRow(records.FindById(31000L + start + offset), start + offset);
            }
            Volatile.Write(ref _written, start + count);
        }

        private void VerifyRow(BsonDocument row, int sequence)
        {
            ((object)row).Should().NotBeNull();
            row["_id"].IsInt64.Should().BeTrue();
            row["_id"].AsInt64.Should().Be(31000L + sequence);
            row["seq"].AsInt32.Should().Be(sequence);
            row["bucket"].AsInt32.Should().Be(sequence % 7);
            row["payload"].AsString.Should().Be(Payload(sequence));
        }

        private int[] SnapshotSequences(ILiteCollection<BsonDocument> records)
        {
            var rows = records.Query().Select("{ _id: $._id, seq: $.seq }").ToArray();
            foreach (var row in rows)
            {
                row["_id"].IsInt64.Should().BeTrue();
                row["_id"].AsInt64.Should().Be(31000L + row["seq"].AsInt32);
            }
            var sequences = rows.Select(row => row["seq"].AsInt32).ToArray();
            sequences.Length.Should().BeGreaterThanOrEqualTo(_seedCount / 2,
                "cleanup always retains at least the newest half of the seed population");
            sequences.Distinct().Count().Should().Be(sequences.Length);
            return sequences;
        }

        private void VerifyPage(BsonDocument[] page, IEnumerable<int> expected)
        {
            page.Select(row => row["seq"].AsInt32).Should().Equal(expected);
            foreach (var row in page) VerifyRow(row, row["seq"].AsInt32);
        }

        private void VerifyAll(LiteDatabase database)
        {
            // Derive the entire surviving set from acknowledged writes/deletes,
            // independently of the database's count, index order, and next auto-ID.
            var expected = Enumerable.Range(0, _written).Where(seq => !_deleted.ContainsKey(seq)).ToArray();
            var records = database.GetCollection("records", BsonAutoId.Int64);
            var rows = records.FindAll().OrderBy(row => row["seq"].AsInt32).ToArray();
            VerifyPage(rows, expected);
            records.Count().Should().Be(expected.Length);
            foreach (var sequence in expected) VerifyRow(records.FindById(31000L + sequence), sequence);
            foreach (var sequence in _deleted.Keys) ((object)records.FindById(31000L + sequence)).Should().BeNull();
            records.Find(Query.EQ("bucket", 3)).Select(row => row["seq"].AsInt32).OrderBy(seq => seq)
                .Should().Equal(expected.Where(seq => seq % 7 == 3));
        }

        private static byte[] FileBytes(int batch) => Enumerable.Range(0, 65536)
            .Select(index => (byte)((index * 31 + batch * 17) % 251)).ToArray();

        private void UploadFile(int batch)
        {
            using var input = new MemoryStream(FileBytes(batch), writable: false);
            _files.FileStorage.Upload("batch-" + batch, "batch-" + batch + ".bin", input);
        }

        private static void VerifyFiles(LiteDatabase files)
        {
            files.FileStorage.FindAll().Select(file => file.Id).OrderBy(id => id)
                .Should().Equal(Enumerable.Range(0, Batches).Select(batch => "batch-" + batch).OrderBy(id => id));
            for (var batch = 0; batch < Batches; batch++)
            {
                var file = files.FileStorage.FindById("batch-" + batch);
                file.Filename.Should().Be("batch-" + batch + ".bin");
                file.Length.Should().Be(65536);
                using var output = new MemoryStream();
                using var input = file.OpenRead();
                input.CopyTo(output);
                output.ToArray().Should().Equal(FileBytes(batch));
            }
        }

        public void VerifyReopened(string path, string storagePath)
        {
            using (var reopened = new LiteDatabase(path))
            using (var files = new LiteDatabase(storagePath))
            {
                VerifyAll(reopened);
                VerifyFiles(files);
                var records = reopened.GetCollection("records", BsonAutoId.Int64);
                records.Insert(Row(_written)).AsInt64.Should().Be(31000L + _written);
                _written++;
                VerifyAll(reopened);
                reopened.Checkpoint();
            }
            var image = File.ReadAllBytes(path);
            using (var readOnly = new LiteDatabase(new ConnectionString { Filename = path, ReadOnly = true }))
            {
                VerifyAll(readOnly);
            }
            File.ReadAllBytes(path).Should().Equal(image);
            Console.WriteLine($"PERSISTENCE_2043: recoveryId={31000L + _written - 1}, " +
                $"databaseBytes={image.Length}, reopens=2, readOnlyBytesUnchanged=true");
        }
    }
}
