using System.Linq;

namespace LiteDB;

public class BsonVector(float[] values) : BsonValue(values)
{
    public float[] Values => AsVector;

    public BsonValue Clone()
    {
        return new BsonVector((float[])Values.Clone());
    }

    public override string ToString()
    {
        return $"[{string.Join(", ", Values.Select(v => v.ToString("0.###")))}]";
    }
}
