using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using LiteDB.Benchmarks.Models.Spatial;
using LiteDB.Spatial;
using SpatialApi = LiteDB.Spatial.Spatial;

namespace LiteDB.Benchmarks.Benchmarks.Spatial
{
    [BenchmarkCategory(Constants.Categories.QUERIES)]
    public class SpatialQueryBenchmarks : BenchmarkBase
    {
        private ILiteCollection<SpatialDocument> _collection = null!;
        private GeoPoint _center = null!;
        private GeoPolygon _searchArea = null!;
        private double _radiusMeters;

        [GlobalSetup]
        public void GlobalSetup()
        {
            File.Delete(DatabasePath);

            DatabaseInstance = new LiteDatabase(ConnectionString());
            _collection = DatabaseInstance.GetCollection<SpatialDocument>("places");

            SpatialApi.EnsurePointIndex(_collection, x => x.Location);
            SpatialApi.EnsureShapeIndex(_collection, x => x.Region);
            SpatialApi.EnsureShapeIndex(_collection, x => x.Route);

            var documents = SpatialDocumentGenerator.Generate(DatasetSize);
            _collection.Insert(documents);

            DatabaseInstance.Checkpoint();

            _center = new GeoPoint(0, 0);
            _radiusMeters = 25_000;
            _searchArea = SpatialDocumentGenerator.BuildSearchPolygon(0, 0, 0.1);
        }

        [Benchmark(Baseline = true)]
        public List<SpatialDocument> NearQuery()
        {
            return SpatialApi.Near(_collection, x => x.Location, _center, _radiusMeters).ToList();
        }

        [Benchmark]
        public List<SpatialDocument> BoundingBoxQuery()
        {
            return SpatialApi.WithinBoundingBox(_collection, x => x.Location, -0.2, -0.2, 0.2, 0.2).ToList();
        }

        [Benchmark]
        public List<SpatialDocument> PolygonContainmentQuery()
        {
            return SpatialApi.Within(_collection, x => x.Region, _searchArea).ToList();
        }

        [Benchmark]
        public List<SpatialDocument> RouteIntersectionQuery()
        {
            return SpatialApi.Intersects(_collection, x => x.Route, _searchArea).ToList();
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
