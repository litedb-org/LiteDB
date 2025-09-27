using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using LiteDB;
using LiteDB.Benchmarks.Models;
using LiteDB.Benchmarks.Models.Generators;

namespace LiteDB.Benchmarks.Benchmarks.Queries
{
    [BenchmarkCategory(Constants.Categories.QUERIES)]
    public class QueryWithVectorSimilarity : BenchmarkBase
    {
        private ILiteCollection<FileMetaBase> _fileMetaCollection;
        private ILiteCollection<FileMetaBase> _unindexedCollection;
        private float[] _queryVector;

        [GlobalSetup]
        public void GlobalSetup()
        {
            File.Delete(DatabasePath);

            DatabaseInstance = new LiteDatabase(ConnectionString());
            _fileMetaCollection = DatabaseInstance.GetCollection<FileMetaBase>("withIndex");
            _unindexedCollection = DatabaseInstance.GetCollection<FileMetaBase>("withoutIndex");

            _fileMetaCollection.EnsureIndex(fileMeta => fileMeta.ShouldBeShown);
            _unindexedCollection.EnsureIndex(fileMeta => fileMeta.ShouldBeShown);
            _fileMetaCollection.EnsureIndex(fileMeta => fileMeta.Vectors, new VectorIndexOptions(128));

            var rnd = new Random();
            var data = FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize);

            _fileMetaCollection.Insert(data); // executed once per each N value
            _unindexedCollection.Insert(data);

            _queryVector = Enumerable.Range(0, 128).Select(_ => (float)rnd.NextDouble()).ToArray();

            DatabaseInstance.Checkpoint();
        }

        [Benchmark]
        public List<FileMetaBase> WhereNear_Filter()
        {
            return _unindexedCollection.Query()
                .WhereNear(x => x.Vectors, _queryVector, maxDistance: 0.5)
                .ToList();
        }

        [Benchmark]
        public List<FileMetaBase> WhereNear_Filter_Indexed()
        {
            return _fileMetaCollection.Query()
                .WhereNear(x => x.Vectors, _queryVector, maxDistance: 0.5)
                .ToList();
        }

        [Benchmark]
        public List<FileMetaBase> TopKNear_OrderLimit()
        {
            return _unindexedCollection.Query()
                .TopKNear(x => x.Vectors, _queryVector, k: 10)
                .ToList();
        }

        [Benchmark]
        public List<FileMetaBase> TopKNear_OrderLimit_Indexed()
        {
            return _fileMetaCollection.Query()
                .TopKNear(x => x.Vectors, _queryVector, k: 10)
                .ToList();
        }
    }
}