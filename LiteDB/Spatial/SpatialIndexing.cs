using System;
using System.Collections.Generic;

namespace LiteDB.Spatial
{
    internal static class SpatialIndexing
    {
        public static long ComputeMorton(GeoPoint point, int precisionBits)
        {
            if (point == null)
            {
                throw new ArgumentNullException(nameof(point));
            }

            if (precisionBits <= 0 || precisionBits > 60)
            {
                throw new ArgumentOutOfRangeException(nameof(precisionBits), "Precision must be between 1 and 60 bits");
            }

            var normalized = point.Normalize();
            var bitsPerCoordinate = Math.Max(1, precisionBits / 2);
            var scaleLat = (1UL << bitsPerCoordinate) - 1UL;
            var scaleLon = (1UL << bitsPerCoordinate) - 1UL;

            var latNormalized = (normalized.Lat + 90d) / 180d;
            var lonNormalized = (normalized.Lon + 180d) / 360d;

            latNormalized = Math.Min(1d, Math.Max(0d, latNormalized));
            lonNormalized = Math.Min(1d, Math.Max(0d, lonNormalized));

            var latBits = (ulong)Math.Round(latNormalized * scaleLat);
            var lonBits = (ulong)Math.Round(lonNormalized * scaleLon);

            var morton = Interleave(lonBits, latBits, bitsPerCoordinate);

            return unchecked((long)morton);
        }

        private static ulong Interleave(ulong x, ulong y, int bits)
        {
            ulong result = 0;

            for (var i = 0; i < bits; i++)
            {
                var shift = (ulong)i;
                result |= ((x >> i) & 1UL) << (int)(2 * shift);
                result |= ((y >> i) & 1UL) << (int)(2 * shift + 1);
            }

            return result;
        }

        public static IReadOnlyList<(long Start, long End)> CoverBoundingBox(GeoBoundingBox box, int precisionBits, int maxCells)
        {
            if (precisionBits <= 0)
            {
                return Array.Empty<(long Start, long End)>();
            }

            maxCells = Math.Max(1, maxCells);

            var segments = SplitBoundingBox(box);
            var ranges = new List<(long Start, long End)>();

            var latCells = Math.Max(1, (int)Math.Round(Math.Sqrt(maxCells)));
            var lonCells = Math.Max(1, maxCells / latCells);

            foreach (var segment in segments)
            {
                var latStep = (segment.MaxLat - segment.MinLat) / latCells;
                var lonStep = (segment.MaxLon - segment.MinLon) / lonCells;

                if (latStep == 0)
                {
                    latStep = segment.MaxLat - segment.MinLat;
                }

                if (lonStep == 0)
                {
                    lonStep = segment.MaxLon - segment.MinLon;
                }

                for (var latIndex = 0; latIndex < latCells; latIndex++)
                {
                    var minLat = segment.MinLat + latStep * latIndex;
                    var maxLat = latIndex == latCells - 1 ? segment.MaxLat : minLat + latStep;

                    for (var lonIndex = 0; lonIndex < lonCells; lonIndex++)
                    {
                        var minLon = segment.MinLon + lonStep * lonIndex;
                        var maxLon = lonIndex == lonCells - 1 ? segment.MaxLon : minLon + lonStep;

                        var start = ComputeMorton(new GeoPoint(minLat, minLon), precisionBits);
                        var end = ComputeMorton(new GeoPoint(maxLat, maxLon), precisionBits);

                        if (start > end)
                        {
                            (start, end) = (end, start);
                        }

                        ranges.Add((start, end));
                    }
                }
            }

            return MergeRanges(ranges);
        }

        public static IReadOnlyList<GeoBoundingBox> SplitBoundingBox(GeoBoundingBox box)
        {
            if (box.MaxLon >= box.MinLon)
            {
                return new[] { box };
            }

            var first = new GeoBoundingBox(box.MinLat, box.MinLon, box.MaxLat, 180d);
            var second = new GeoBoundingBox(box.MinLat, -180d, box.MaxLat, box.MaxLon);

            return new[] { first, second };
        }

        private static IReadOnlyList<(long Start, long End)> MergeRanges(List<(long Start, long End)> ranges)
        {
            if (ranges.Count == 0)
            {
                return ranges;
            }

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<(long Start, long End)>(ranges.Count);
            var current = ranges[0];

            for (var i = 1; i < ranges.Count; i++)
            {
                var next = ranges[i];

                if (next.Start <= current.End + 1)
                {
                    current = (current.Start, Math.Max(current.End, next.End));
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);

            return merged;
        }
    }
}
