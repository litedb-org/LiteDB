using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using LiteDB.Benchmarks.Models;
using LiteDB.Benchmarks.Models.Generators;

namespace LiteDB.Benchmarks.Benchmarks.Insertion
{
	[BenchmarkCategory(Constants.Categories.INSERTION)]
	public class InsertionIgnoreExpressionPropertyBenchmark : BenchmarkBase
	{
		private List<FileMetaBase> _baseData;
		private List<FileMetaWithExclusion> _baseDataWithBsonIgnore;

		private ILiteCollection<FileMetaBase> _fileMetaCollection;
		private ILiteCollection<FileMetaWithExclusion> _fileMetaExclusionCollection;

		[GlobalSetup(Target = nameof(Insertion))]
		public void GlobalBsonIgnoreSetup()
		{
			File.Delete(DatabasePath);

			DatabaseInstance = new LiteDatabase(ConnectionString());
			_fileMetaCollection = DatabaseInstance.GetCollection<FileMetaBase>();
			_fileMetaCollection.EnsureIndex(fileMeta => fileMeta.ShouldBeShown);

			_baseData = FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize); // executed once per each N value
		}

		[GlobalSetup(Target = nameof(InsertionWithBsonIgnore))]
		public void GlobalIgnorePropertySetup()
		{
			File.Delete(DatabasePath);

			DatabaseInstance = new LiteDatabase(ConnectionString());
			_fileMetaExclusionCollection = DatabaseInstance.GetCollection<FileMetaWithExclusion>();
			_fileMetaExclusionCollection.EnsureIndex(fileMeta => fileMeta.ShouldBeShown);

			_baseDataWithBsonIgnore = FileMetaGenerator<FileMetaWithExclusion>.GenerateList(DatasetSize); // executed once per each N value
		}

		[Benchmark(Baseline = true)]
		public int Insertion()
		{
			var count = _fileMetaCollection.Insert(_baseData);
			DatabaseInstance.Checkpoint();
			return count;
		}

		[Benchmark]
		public int InsertionWithBsonIgnore()
		{
			var count = _fileMetaExclusionCollection.Insert(_baseDataWithBsonIgnore);
			DatabaseInstance.Checkpoint();
			return count;
		}

		[IterationCleanup]
		public void IterationCleanup()
		{
			var indexesCollection = DatabaseInstance.GetCollection("$indexes");
			var droppedCollectionIndexes = indexesCollection.Query().Where(x => x["name"] != "_id").ToDocuments().ToList();

			var collectionNames = DatabaseInstance.GetCollectionNames();
			foreach (var name in collectionNames)
			{
				DatabaseInstance.DropCollection(name);
			}

			foreach (var indexInfo in droppedCollectionIndexes)
			{
				DatabaseInstance.GetCollection(indexInfo["collection"])
					.EnsureIndex(indexInfo["name"], BsonExpression.Create(indexInfo["expression"]), indexInfo["unique"]);
			}

			DatabaseInstance.Checkpoint();
			DatabaseInstance.Rebuild();
		}

		[GlobalCleanup]
		public void GlobalCleanup()
		{
			_baseData?.Clear();
			_baseData = null;

			_baseDataWithBsonIgnore?.Clear();
			_baseDataWithBsonIgnore = null;

			DatabaseInstance?.Checkpoint();
			DatabaseInstance?.Dispose();
			DatabaseInstance = null;

			File.Delete(DatabasePath);
		}
	}
}