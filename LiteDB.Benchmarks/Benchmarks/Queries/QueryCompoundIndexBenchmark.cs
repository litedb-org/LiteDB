using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using LiteDB.Benchmarks.Models;
using LiteDB.Benchmarks.Models.Generators;

namespace LiteDB.Benchmarks.Benchmarks.Queries
{
	[BenchmarkCategory(Constants.Categories.QUERIES)]
	public class QueryCompoundIndexBenchmark : BenchmarkBase
	{
		private const string COMPOUND_INDEX_NAME = "CompoundIndex1";

		private ILiteCollection<FileMetaBase> _fileMetaCollection;

		[GlobalSetup(Target = nameof(Query_SimpleIndex_Baseline))]
		public void GlobalSetupSimpleIndexBaseline()
		{
			File.Delete(DatabasePath);

			DatabaseInstance = new LiteDatabase(ConnectionString());
			_fileMetaCollection = DatabaseInstance.GetCollection<FileMetaBase>();
			_fileMetaCollection.EnsureIndex(fileMeta => fileMeta.ShouldBeShown);
			_fileMetaCollection.EnsureIndex(fileMeta => fileMeta.IsFavorite);

			_fileMetaCollection.Insert(FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize)); // executed once per each N value

			DatabaseInstance.Checkpoint();
		}

		[GlobalSetup(Target = nameof(Query_CompoundIndexVariant))]
		public void GlobalSetupCompoundIndexVariant()
		{
			DatabaseInstance = new LiteDatabase(ConnectionString());
			_fileMetaCollection = DatabaseInstance.GetCollection<FileMetaBase>();
			_fileMetaCollection.EnsureIndex(COMPOUND_INDEX_NAME, $"$.{nameof(FileMetaBase.IsFavorite)};$.{nameof(FileMetaBase.ShouldBeShown)}");

			_fileMetaCollection.Insert(FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize)); // executed once per each N value

			DatabaseInstance.Checkpoint();
		}

		[Benchmark(Baseline = true)]
		public List<FileMetaBase> Query_SimpleIndex_Baseline()
		{
			return _fileMetaCollection.Find(Query.And(
					Query.EQ(nameof(FileMetaBase.IsFavorite), false),
					Query.EQ(nameof(FileMetaBase.ShouldBeShown), true)))
				.ToList();
		}

		[Benchmark]
		public List<FileMetaBase> Query_CompoundIndexVariant()
		{
			return _fileMetaCollection.Find(Query.EQ(COMPOUND_INDEX_NAME, $"{false};{true}")).ToList();
		}

		[GlobalCleanup]
		public void GlobalCleanup()
		{
			// Disposing logic
			DatabaseInstance?.Checkpoint();
			DatabaseInstance?.Dispose();
			DatabaseInstance = null;

			File.Delete(DatabasePath);
		}
	}
}