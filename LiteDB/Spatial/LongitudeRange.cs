using System;

namespace LiteDB.Spatial
{
    internal readonly struct LongitudeRange
    {
        private readonly double _start;
        private readonly double _end;
        private readonly bool _wraps;

        public LongitudeRange(double start, double end)
        {
            _start = GeoMath.NormalizeLongitude(start);
            _end = GeoMath.NormalizeLongitude(end);
            _wraps = _start > _end;
        }

        public bool Contains(double lon)
        {
            lon = GeoMath.NormalizeLongitude(lon);

            if (!_wraps)
            {
                return lon >= _start && lon <= _end;
            }

            return lon >= _start || lon <= _end;
        }

        public bool Intersects(LongitudeRange other)
        {
            if (!_wraps && !other._wraps)
            {
                return !(_start > other._end || other._start > _end);
            }

            for (var i = 0; i < 2; i++)
            {
                var aStart = i == 0 ? _start : -180d;
                var aEnd = i == 0 ? (_wraps ? 180d : _end) : _end;

                if (aStart > aEnd)
                {
                    continue;
                }

                for (var j = 0; j < 2; j++)
                {
                    var bStart = j == 0 ? other._start : -180d;
                    var bEnd = j == 0 ? (other._wraps ? 180d : other._end) : other._end;

                    if (bStart > bEnd)
                    {
                        continue;
                    }

                    if (!(aStart > bEnd || bStart > aEnd))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
