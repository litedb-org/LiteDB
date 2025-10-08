using System.Linq;

namespace LiteDB;

public class BsonVector(float[] values) : BsonValue(values)
{
    public float[] Values => AsVector;

    public BsonValue Clone()
    {
        return new BsonVector((float[])Values.Clone());
    }

    public override bool Equals(object? obj)
    {
        return obj is BsonVector other && Values.SequenceEqual(other.Values);
    }

    public override int GetHashCode()
    {
        return Values.Aggregate(17, (acc, f) => acc * 31 + f.GetHashCode());
    }

    public override string ToString()
    {
        return $"[{string.Join(", ", Values.Select(v => v.ToString("0.###")))}]";
    }
}