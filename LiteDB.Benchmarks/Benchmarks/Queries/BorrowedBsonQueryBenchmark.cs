using System.IO;
using System.Linq;

using BenchmarkDotNet.Attributes;

namespace LiteDB.Benchmarks.Benchmarks.Queries
{
    /// <summary>
    /// End-to-end scans over large documents. Each benchmark consumes its result
    /// so allocation measurements include the complete query pipeline.
    /// </summary>
    [BenchmarkCategory(Constants.Categories.QUERIES)]
    public class BorrowedBsonQueryBenchmark
    {
        private MemoryStream _stream;
        private LiteDatabase _database;
        private ILiteCollection<BsonDocument> _collection;
        private int _lastScore;

        [Params(10_000)]
        public int DatasetSize { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _stream = new MemoryStream();
            _database = new LiteDatabase(_stream);
            _collection = _database.GetCollection("items", BsonAutoId.Int32);

            var documents = Enumerable.Range(0, DatasetSize).Select(index =>
            {
                var score = (index * 7_919) % DatasetSize;

                return new BsonDocument
                {
                    ["Score"] = score,
                    ["Name"] = "item-" + index,
                    ["Payload"] = new string('x', 1024),
                    ["Metadata"] = new BsonDocument
                    {
                        ["Group"] = index % 10,
                        ["Description"] = new string('y', 256)
                    }
                };
            });

            _collection.InsertBulk(documents);
            _database.Checkpoint();
            _lastScore = ((DatasetSize - 1) * 7_919) % DatasetSize;
        }

        [Benchmark]
        public int SelectiveCount()
        {
            return _collection.Count($"$.Score >= {DatasetSize - 100}");
        }

        [Benchmark]
        public bool LateExists()
        {
            return _collection.Exists($"$.Score = {_lastScore}");
        }

        [Benchmark]
        public int SelectiveDocuments()
        {
            return _collection.Query()
                .Where($"$.Score >= {DatasetSize - 100}")
                .Limit(10)
                .ToList()
                .Sum(document => document["Score"].AsInt32);
        }

        [Benchmark]
        public int SmallProjection()
        {
            return _collection.Query()
                .Where($"$.Score >= {DatasetSize - 100}")
                .Select("{ Name: $.Name, Score: $.Score }")
                .ToList()
                .Sum(document => document["Score"].AsInt32);
        }

        [Benchmark]
        public int SortAndLimit()
        {
            return _collection.Query()
                .OrderBy("$.Score")
                .Limit(20)
                .ToList()
                .Sum(document => document["Score"].AsInt32);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _database?.Dispose();
            _stream?.Dispose();
        }
    }
}
