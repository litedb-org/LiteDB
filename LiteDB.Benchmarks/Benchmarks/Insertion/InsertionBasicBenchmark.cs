using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using LiteDB.Benchmarks.Models;
using LiteDB.Benchmarks.Models.Generators;

namespace LiteDB.Benchmarks.Benchmarks.Insertion
{
	[BenchmarkCategory(Constants.Categories.INSERTION)]
	public class InsertionBasicBenchmark : BenchmarkBase
	{
		private List<FileMetaBase> _data;
		private ILiteCollection<FileMetaBase> _fileMetaCollection;

		[GlobalSetup]
		public void GlobalSetup()
		{
			File.Delete(DatabasePath);

			DatabaseInstance = new LiteDatabase(ConnectionString());
			_fileMetaCollection = DatabaseInstance.GetCollection<FileMetaBase>();

			_data = FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize); // executed once per each N value
		}

		[Benchmark(Baseline = true)]
		public int Insertion()
		{
			var count = _fileMetaCollection.Insert(_data);
			DatabaseInstance.Checkpoint();
			return count;
		}

		[Benchmark]
		public void InsertionWithLoop()
		{
			// ReSharper disable once ForCanBeConvertedToForeach
			for (var i = 0; i < _data.Count; i++)
			{
				_fileMetaCollection.Insert(_data[i]);
			}

			DatabaseInstance.Checkpoint();
		}

		[Benchmark]
		public int Upsertion()
		{
			var count = _fileMetaCollection.Upsert(_data);
			DatabaseInstance.Checkpoint();
			return count;
		}

		[Benchmark]
		public void UpsertionWithLoop()
		{
			// ReSharper disable once ForCanBeConvertedToForeach
			for (var i = 0; i < _data.Count; i++)
			{
				_fileMetaCollection.Upsert(_data[i]);
			}

			DatabaseInstance.Checkpoint();
		}

		[IterationCleanup]
		public void IterationCleanup()
		{
			const string collectionName = nameof(FileMetaBase);

			DatabaseInstance.DropCollection(collectionName);

			DatabaseInstance.Checkpoint();
			DatabaseInstance.Rebuild();
		}

		[GlobalCleanup]
		public void GlobalCleanup()
		{
			DatabaseInstance?.Checkpoint();
			DatabaseInstance?.Dispose();
			DatabaseInstance = null;

			File.Delete(DatabasePath);
		}
	}
}