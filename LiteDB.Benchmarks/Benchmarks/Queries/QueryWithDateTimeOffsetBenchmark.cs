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
	public class QueryWithDateTimeOffsetBenchmark : BenchmarkBase
	{
		private DateTime _dateTimeConstraint;
		private BsonValue _dateTimeConstraintBsonValue;

		private ILiteCollection<FileMetaBase> _fileMetaCollection;

		[GlobalSetup]
		public void GlobalSetup()
		{
			File.Delete(DatabasePath);

			DatabaseInstance = new LiteDatabase(ConnectionString());
			_fileMetaCollection = DatabaseInstance.GetCollection<FileMetaBase>();
			_fileMetaCollection.EnsureIndex(fileMeta => fileMeta.ValidFrom);
			_fileMetaCollection.EnsureIndex(fileMeta => fileMeta.ValidTo);
			_fileMetaCollection.EnsureIndex(fileMeta => fileMeta.ShouldBeShown);

			_fileMetaCollection.Insert(FileMetaGenerator<FileMetaBase>.GenerateList(DatasetSize)); // executed once per each N value

			DatabaseInstance.Checkpoint();

			_dateTimeConstraint = DateTime.Now;
			_dateTimeConstraintBsonValue = new BsonValue(_dateTimeConstraint);
		}

		[Benchmark(Baseline = true)]
		public List<FileMetaBase> Expression_Normal_Baseline()
		{
			return _fileMetaCollection.Find(fileMeta =>
				(fileMeta.ValidFrom > _dateTimeConstraint || fileMeta.ValidTo < _dateTimeConstraint) && fileMeta.ShouldBeShown).ToList();
		}

		[Benchmark]
		public List<FileMetaBase> Query_Normal()
		{
			return _fileMetaCollection.Find(Query.And(
					Query.Or(
						Query.GT(nameof(FileMetaBase.ValidFrom), _dateTimeConstraintBsonValue),
						Query.LT(nameof(FileMetaBase.ValidTo), _dateTimeConstraintBsonValue)),
					Query.EQ(nameof(FileMetaBase.ShouldBeShown), true)))
				.ToList();
		}

		[Benchmark]
		public List<FileMetaBase> Expression_ParametersSwitched()
		{
			return _fileMetaCollection.Find(fileMeta =>
				fileMeta.ShouldBeShown && (fileMeta.ValidFrom > _dateTimeConstraint || fileMeta.ValidTo < _dateTimeConstraint)).ToList();
		}

		[Benchmark]
		public List<FileMetaBase> Query_ParametersSwitched()
		{
			return _fileMetaCollection.Find(Query.And(
					Query.EQ(nameof(FileMetaBase.ShouldBeShown), true),
					Query.Or(
						Query.GT(nameof(FileMetaBase.ValidFrom), _dateTimeConstraintBsonValue),
						Query.LT(nameof(FileMetaBase.ValidTo), _dateTimeConstraintBsonValue))))
				.ToList();
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