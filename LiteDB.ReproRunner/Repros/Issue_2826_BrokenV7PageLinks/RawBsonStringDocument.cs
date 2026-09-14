internal static class RawBsonStringDocument
{
    private const byte StringType = 0x02;

    public static IReadOnlyDictionary<string, string> Read(byte[] bytes)
    {
        var cursor = new RawCursor(bytes, 0, bytes.Length);
        var declaredLength = cursor.ReadInt32();
        Require(declaredLength == bytes.Length, "BSON length does not match the raw extend data length");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (cursor.Remaining > 1)
        {
            Require(cursor.ReadByte() == StringType, "fixture BSON contains a non-string field");
            var name = cursor.ReadCString();
            var valueLength = cursor.ReadInt32();
            Require(valueLength > 0, "fixture BSON string has an invalid length");
            var encoded = cursor.ReadBytes(valueLength);
            Require(encoded[^1] == 0, "fixture BSON string is not null terminated");

            var valueCursor = new RawCursor(encoded, 0, encoded.Length - 1);
            var value = valueCursor.ReadBytes(valueCursor.Remaining);
            Require(values.TryAdd(name, System.Text.Encoding.UTF8.GetString(value)),
                "fixture BSON contains a duplicate field: " + name);
        }

        Require(cursor.Remaining == 1 && cursor.ReadByte() == 0,
            "fixture BSON does not have exactly one document terminator");
        return values;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
