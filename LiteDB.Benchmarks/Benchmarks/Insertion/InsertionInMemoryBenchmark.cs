using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using LiteDB.Benchmarks.Models;
using LiteDB.Benchmarks.Models.Generators;

namespace LiteDB.Benchmarks.Benchmarks.Insertion
{
	[BenchmarkCategory(Constants.Categories.INSERTION)]
	public class InsertionInMemoryBenchmark : BenchmarkBase
	{
		private List<FileMetaBase> _data;

		private ILiteDatabase _databaseInstanceNormal;
		private ILiteDatabase _databaseInstanceInMemory;
		private ILiteCollection<FileMetaBase> _fileMetaNormalCollection;
		private ILiteCollection<FileMetaBase> _fileMetaInMemoryCollection;

		[GlobalSetup(Target = nameof(InsertionNormal))]
		public void GlobalSetupNormal()
		{
			File.Delete(DatabasePath);

			_data = FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize); // executed once per each N value

			_databaseInstanceNormal = new LiteDatabase(ConnectionString());
			_fileMetaNormalCollection = _databaseInstanceNormal.GetCollection<FileMetaBase>();
		}

		[GlobalSetup(Target = nameof(InsertionInMemory))]
		public void GlobalSetupInMemory()
		{
			_data = FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize); // executed once per each N value

			_databaseInstanceInMemory = new LiteDatabase(new MemoryStream());
			_fileMetaInMemoryCollection = _databaseInstanceInMemory.GetCollection<FileMetaBase>();
		}

		[Benchmark(Baseline = true)]
		public int InsertionNormal()
		{
			var count = _fileMetaNormalCollection.Insert(_data);
			_databaseInstanceNormal.Checkpoint();
			return count;
		}

		[Benchmark]
		public int InsertionInMemory()
		{
			var count = _fileMetaInMemoryCollection.Insert(_data);
			_databaseInstanceNormal.Checkpoint();
			return count;
		}

		[IterationCleanup(Target = nameof(InsertionNormal))]
		public void CleanUpNormal()
		{
			const string collectionName = nameof(FileMetaBase);

			var indexesCollection = _databaseInstanceNormal.GetCollection("$indexes");
			var droppedCollectionIndexes = indexesCollection.Query().Where(x => x["collection"] == collectionName && x["name"] != "_id").ToDocuments().ToList();

			_databaseInstanceNormal.DropCollection(collectionName);

			foreach (var indexInfo in droppedCollectionIndexes)
			{
				_databaseInstanceNormal.GetCollection(collectionName)
					.EnsureIndex(indexInfo["name"], BsonExpression.Create(indexInfo["expression"]), indexInfo["unique"]);
			}

			_databaseInstanceNormal.Checkpoint();
			_databaseInstanceNormal.Rebuild();
		}

		[IterationCleanup(Target = nameof(InsertionInMemory))]
		public void CleanUpInMemory()
		{
			const string collectionName = nameof(FileMetaBase);

			var indexesCollection = _databaseInstanceInMemory.GetCollection("$indexes");
			var droppedCollectionIndexes = indexesCollection.Query().Where(x => x["collection"] == collectionName && x["name"] != "_id").ToDocuments().ToList();

			_databaseInstanceInMemory.DropCollection(collectionName);

			foreach (var indexInfo in droppedCollectionIndexes)
			{
				_databaseInstanceInMemory.GetCollection(collectionName)
					.EnsureIndex(indexInfo["name"], BsonExpression.Create(indexInfo["expression"]), indexInfo["unique"]);
			}

			_databaseInstanceInMemory.Checkpoint();
			_databaseInstanceInMemory.Rebuild();
		}

		[GlobalCleanup(Target = nameof(InsertionNormal))]
		public void GlobalCleanupNormal()
		{
			_fileMetaNormalCollection = null;

			_databaseInstanceNormal?.Checkpoint();
			_databaseInstanceNormal?.Dispose();
			_databaseInstanceNormal = null;

			File.Delete(DatabasePath);
		}

		[GlobalCleanup(Target = nameof(InsertionInMemory))]
		public void GlobalCleanupInMemory()
		{
			_fileMetaInMemoryCollection = null;

			_databaseInstanceInMemory?.Checkpoint();
			_databaseInstanceInMemory?.Dispose();
			_databaseInstanceInMemory = null;
		}
	}
}