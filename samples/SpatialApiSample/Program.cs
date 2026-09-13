using LiteDB;
using LiteDB.Spatial;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

const string DatabasePath = "spatial-sample.db";

app.MapPost("/seed", () =>
{
    using var db = new LiteDatabase(DatabasePath);
    var places = db.GetCollection<Place>("places");
    Spatial.EnsurePointIndex(places, x => x.Location);
    Spatial.EnsureShapeIndex(places, x => x.Coverage);

    if (places.Count() > 0)
    {
        return Results.Ok(new { message = "Database already seeded." });
    }

    var vienna = new Place
    {
        Name = "Vienna",
        Location = new GeoPoint(48.2082, 16.3738),
        Coverage = SquareAround(48.2082, 16.3738, 0.2)
    };

    var bratislava = new Place
    {
        Name = "Bratislava",
        Location = new GeoPoint(48.1486, 17.1077),
        Coverage = SquareAround(48.1486, 17.1077, 0.15)
    };

    places.Insert(new[] { vienna, bratislava });

    return Results.Ok(new { message = "Seeded" });
});

app.MapGet("/places/near", (double lat, double lon, double radiusKm) =>
{
    using var db = new LiteDatabase(DatabasePath);
    var places = db.GetCollection<Place>("places");
    Spatial.EnsurePointIndex(places, x => x.Location);

    var center = new GeoPoint(lat, lon);
    var radiusMeters = radiusKm * 1000;

    var results = Spatial.Near(places, x => x.Location, center, radiusMeters)
        .Select(x => new { x.Name, x.Location.Lat, x.Location.Lon })
        .ToList();

    return Results.Ok(results);
});

app.MapGet("/places/within", () =>
{
    using var db = new LiteDatabase(DatabasePath);
    var places = db.GetCollection<Place>("places");
    Spatial.EnsureShapeIndex(places, x => x.Coverage);

    var polygon = SquareAround(48.2, 16.35, 0.25);

    var results = Spatial.Within(places, x => x.Coverage, polygon)
        .Select(x => new { x.Name })
        .ToList();

    return Results.Ok(results);
});

app.Run();

static GeoPolygon SquareAround(double lat, double lon, double halfExtent)
{
    var topLeft = new GeoPoint(lat + halfExtent, lon - halfExtent);
    var topRight = new GeoPoint(lat + halfExtent, lon + halfExtent);
    var bottomRight = new GeoPoint(lat - halfExtent, lon + halfExtent);
    var bottomLeft = new GeoPoint(lat - halfExtent, lon - halfExtent);

    return new GeoPolygon(new[]
    {
        topLeft,
        topRight,
        bottomRight,
        bottomLeft,
        topLeft
    });
}

public class Place
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public GeoPoint Location { get; set; } = new GeoPoint(0, 0);

    public GeoPolygon Coverage { get; set; } = SquareAround(0, 0, 0.1);

    internal long _gh { get; set; }

    internal double[] _mbb { get; set; } = Array.Empty<double>();
}
