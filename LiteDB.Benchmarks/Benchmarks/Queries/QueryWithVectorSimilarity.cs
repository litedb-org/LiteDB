using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using LiteDB.Benchmarks.Models;
using LiteDB.Benchmarks.Models.Generators;

namespace LiteDB.Benchmarks.Benchmarks.Queries
{
    [BenchmarkCategory(Constants.Categories.QUERIES)]
    public class QueryWithVectorSimilarity : BenchmarkBase
    {
        private ILiteCollection<FileMetaBase> _fileMetaCollection;
        private float[] _queryVector;

        [GlobalSetup]
        public void GlobalSetup()
        {
            File.Delete(DatabasePath);

            DatabaseInstance = new LiteDatabase(ConnectionString());
            _fileMetaCollection = DatabaseInstance.GetCollection<FileMetaBase>();
            _fileMetaCollection.EnsureIndex(fileMeta => fileMeta.ShouldBeShown);

            var rnd = new Random();

            _fileMetaCollection.Insert(FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize)); // executed once per each N value

            _queryVector = Enumerable.Range(0, 128).Select(_ => (float)rnd.NextDouble()).ToArray();

            DatabaseInstance.Checkpoint();
        }

        [Benchmark]
        public List<FileMetaBase> WhereNear_Filter()
        {
            return _fileMetaCollection.Query()
                .WhereNear(x => x.Vectors, _queryVector, maxDistance: 0.5)
                .ToList();
        }

        [Benchmark]
        public List<FileMetaBase> TopKNear_OrderLimit()
        {
            return _fileMetaCollection.Query()
                .TopKNear(x => x.Vectors, _queryVector, k: 10)
                .ToList();
        }
    }
}