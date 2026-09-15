using System;
using System.Linq;

namespace LiteDB;

internal partial class BsonExpressionMethods
{
    public static BsonValue VECTOR_SIM(BsonValue left, BsonValue right)
    {
        if (!(left.IsArray || left.Type == BsonType.Vector) || !(right.IsArray || right.Type == BsonType.Vector))
            return BsonValue.Null;

        var query = left.IsArray
            ? left.AsArray
            : new BsonArray(left.AsVector.Select(x => (BsonValue)x));

        var candidate = right.IsVector
            ? right.AsVector
            : right.AsArray.Select(x =>
            {
                try { return (float)x.AsDouble; }
                catch { return float.NaN; }
            }).ToArray();

        if (query.Count != candidate.Length) return BsonValue.Null;

        double dot = 0, magQ = 0, magC = 0;

        for (int i = 0; i < candidate.Length; i++)
        {
            double q;
            try
            {
                q = query[i].AsDouble;
            }
            catch
            {
                return BsonValue.Null;
            }

            var c = (double)candidate[i];

            if (double.IsNaN(c)) return BsonValue.Null;

            dot += q * c;
            magQ += q * q;
            magC += c * c;
        }

        if (magQ == 0 || magC == 0) return BsonValue.Null;

        var cosine = 1.0 - (dot / (Math.Sqrt(magQ) * Math.Sqrt(magC)));
        return double.IsNaN(cosine) ? BsonValue.Null : cosine;
    }
}