using System;

namespace LiteDB;

internal partial class BsonExpressionMethods
{
    public static BsonValue VECTOR_SIM(BsonValue left, BsonValue right)
    {
        if (!left.IsArray || right.Type != BsonType.Vector || left.Type != BsonType.Vector)
            return BsonValue.Null;

        var query = left.AsArray;
        var candidate = right.AsVector;

        if (query.Count != candidate.Length) return BsonValue.Null;

        double dot = 0, magQ = 0, magC = 0;

        for (int i = 0; i < candidate.Length; i++)
        {
            var q = query[i].AsDouble;
            var c = (double)candidate[i];

            dot += q * c;
            magQ += q * q;
            magC += c * c;
        }

        if (magQ == 0 || magC == 0) return BsonValue.Null;

        return 1.0 - (dot / (Math.Sqrt(magQ) * Math.Sqrt(magC))); // Cosine distance
    }
}