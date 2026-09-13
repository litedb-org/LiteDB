# Spatial Indexing Guide

LiteDB's spatial tooling now supports index-aware queries, expression operators, and ready-to-run samples. This guide walks through enabling spatial indexes, querying data, and composing applications that take advantage of the new API surface.

## 1. Preparing Collections

```csharp
using LiteDB;
using LiteDB.Spatial;

using var db = new LiteDatabase("Filename=geo.db;Mode=Shared");
var places = db.GetCollection<Place>("places");

// Persist precision metadata and computed members
Spatial.EnsurePointIndex(places, x => x.Location);
Spatial.EnsureShapeIndex(places, x => x.Footprint);
```

`EnsurePointIndex` now records the Morton precision that was used so future queries can translate shapes into the correct `_gh` windows without additional configuration.

```csharp
public class Place
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public GeoPoint Location { get; set; } = new GeoPoint(0, 0);
    public GeoPolygon Footprint { get; set; } = SquareAround(0, 0, 0.05);

    internal long _gh { get; set; }
    internal double[] _mbb { get; set; } = Array.Empty<double>();
}

static GeoPolygon SquareAround(double lat, double lon, double halfExtent)
{
    var topLeft = new GeoPoint(lat + halfExtent, lon - halfExtent);
    var topRight = new GeoPoint(lat + halfExtent, lon + halfExtent);
    var bottomRight = new GeoPoint(lat - halfExtent, lon + halfExtent);
    var bottomLeft = new GeoPoint(lat - halfExtent, lon - halfExtent);

    return new GeoPolygon(new[] { topLeft, topRight, bottomRight, bottomLeft, topLeft });
}
```

## 2. Index-Aware Queries

### Radius Searches

`Spatial.Near` now projects circle queries into Morton range scans and `_mbb` filters before falling back to geometry checks. This avoids `FindAll()` enumeration even on large collections.

```csharp
var vienna = new GeoPoint(48.2082, 16.3738);
var withinFiveKm = Spatial.Near(places, x => x.Location, vienna, radiusMeters: 5_000).ToList();
```

### Bounding Boxes

Bounding-box queries reuse the same range generator and anti-meridian aware filters:

```csharp
var hits = Spatial.WithinBoundingBox(places, x => x.Location, 47.9, 16.1, 48.4, 16.6).ToList();
```

### Polygon Containment & Intersections

Shape-based queries can rely on the lightweight `_mbb` predicate that gets folded into the pipeline before precise geometry calculations:

```csharp
var downtown = SquareAround(48.2082, 16.3738, 0.15);
var inside = Spatial.Within(places, x => x.Footprint, downtown).ToList();
```

## 3. Expression & LINQ Operators

Spatial functions participate in the LINQ translator and the expression engine via the new operators:

| C# Call | Bson Expression |
| --- | --- |
| `Spatial.Near(doc.Location, center, 1000)` | `SPATIAL_NEAR($.Location, @0, @1)` |
| `Spatial.Within(doc.Footprint, polygon)` | `SPATIAL_WITHIN($.Footprint, @0)` |
| `Spatial.Intersects(doc.Route, query)` | `SPATIAL_INTERSECTS($.Route, @0)` |
| `Spatial.Contains(doc.Footprint, point)` | `SPATIAL_CONTAINS_POINT($.Footprint, @0)` |

```csharp
var linq = places.Query()
    .Where(p => Spatial.Near(p.Location, vienna, 2_000))
    .Select(p => new { p.Name, p.Location })
    .ToList();
```

Each operator pairs with the indexed `_gh` ranges captured by `EnsurePointIndex`, maintaining fast candidate pruning.

## 4. Benchmarking Spatial Pipelines

`LiteDB.Benchmarks` ships with a `SpatialQueryBenchmarks` suite that tracks radius, bounding-box, containment, and intersection workloads across dataset sizes. Run:

```bash
dotnet run --project LiteDB.Benchmarks -c Release --filter "SpatialQueryBenchmarks"
```

The new benchmarks emit allocations and wall-clock metrics so regressions are easy to spot as spatial features evolve.

## 5. Sample REST API

A minimal API showcasing radius and polygon queries lives under `samples/SpatialApiSample`. Seed and query via:

```bash
dotnet run --project samples/SpatialApiSample
# In another terminal
curl -X POST http://localhost:5000/seed
curl "http://localhost:5000/places/near?lat=48.2&lon=16.37&radiusKm=5"
```

The endpoint reuses the shared helpers, ensuring metadata is created automatically and results arrive sorted by distance.

## 6. Troubleshooting & Options

`SpatialOptions` exposes tunables for query precision and numeric tolerances:

```csharp
Spatial.Options = new SpatialOptions
{
    IndexPrecisionBits = 48,
    NumericToleranceDegrees = 1e-8,
    MaxCoveringCells = 64,
    Distance = DistanceFormula.Vincenty
};
```

Changing `IndexPrecisionBits` updates persisted metadata the next time `EnsurePointIndex` runs, so the engine always knows how to slice query ranges. Adjust `NumericToleranceDegrees` if your datasets require more relaxed comparisons for noisy coordinates.
