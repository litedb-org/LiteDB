using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class Corpus
{
    public const int Count = 256;
    public const string Password = "release-corpus";
    public static readonly string[] Collections = { "integers", "guids", "objectids", "strings" };
    private static readonly string[] Text = { "", "a-b", "ab", "a'b", "a b", "résumé", "resume", "a\u030a", "å", "æ", "ae", "東京", "😀", "Z", "z" };

    public static BsonDocument Document(string collection, int i)
    {
        var bytes = Enumerable.Range(0, i % 9 == 0 ? 20000 : i % 257).Select(n => (byte)(n * 31 + i)).ToArray();
        return new BsonDocument
        {
            ["_id"] = Id(collection, i),
            ["sequence"] = i,
            ["code"] = "code-" + i.ToString("D6"),
            ["text"] = Text[i % Text.Length],
            ["int32"] = i % 2 == 0 ? int.MinValue + i : int.MaxValue - i,
            ["int64"] = i % 2 == 0 ? long.MinValue + i : long.MaxValue - i,
            ["double"] = (i - 128) / 7.0,
            ["decimal"] = (i - 128) / 7m,
            ["boolean"] = i % 2 == 0,
            ["null"] = BsonValue.Null,
            ["date"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(i * 1234567L),
            ["guid"] = Guid.Parse(i.ToString("x8") + "-89ab-cdef-8123-456789abcdef"),
            ["oid"] = new ObjectId(i.ToString("x8") + "1122338000667788"),
            ["binary"] = bytes,
            ["nested"] = new BsonDocument { ["unicode"] = Text[i % Text.Length], ["empty"] = new BsonDocument(),
                ["child"] = new BsonDocument { ["value"] = i, ["missingLike"] = BsonValue.Null } },
            ["array"] = new BsonArray { i, "element", BsonValue.Null, new BsonDocument { ["value"] = -i }, new BsonArray { true, false } },
            ["tags"] = new BsonArray { "group-" + i % 7, "all" },
            ["emptyArray"] = new BsonArray()
        };
    }

    public static BsonValue Id(string collection, int i)
    {
        switch (collection)
        {
            case "guids": return Guid.Parse(i.ToString("x8") + "-89ab-cdef-8123-456789abcdef");
            case "objectids": return new ObjectId((i % 2 == 0 ? "8000" : "0000") + i.ToString("x4") + "1122338000667788");
            case "strings": return "id-" + i.ToString("D6");
            default: return i;
        }
    }
}
