using System;
using System.Linq;
using System.Security.Cryptography;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests;

/// <summary>Structural, type-sensitive BSON assertions and deterministic fingerprints.</summary>
internal static class BsonCompatibilityAssert
{
    public static void AreEqual(BsonValue expected, BsonValue actual) => Compare(expected, actual, "$");

    public static string Fingerprint(BsonDocument document)
    {
        var bytes = BsonSerializer.Serialize(Canonicalize(document).AsDocument);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void Compare(BsonValue expected, BsonValue actual, string path)
    {
        Assert.AreEqual(expected.Type, actual.Type, $"BSON type differs at {path}.");

        if (expected.IsDocument)
        {
            var expectedDocument = expected.AsDocument;
            var actualDocument = actual.AsDocument;
            CollectionAssert.AreEquivalent(expectedDocument.Keys.ToArray(), actualDocument.Keys.ToArray(),
                $"Fields differ at {path}; omitted and explicit null fields are distinct.");
            foreach (var key in expectedDocument.Keys)
            {
                Assert.IsTrue(actualDocument.ContainsKey(key), $"Missing field {path}.{key}.");
                Compare(expectedDocument[key], actualDocument[key], $"{path}.{key}");
            }
            return;
        }

        if (expected.IsArray)
        {
            Assert.AreEqual(expected.AsArray.Count, actual.AsArray.Count, $"Array length differs at {path}.");
            for (var index = 0; index < expected.AsArray.Count; index++)
            {
                Compare(expected.AsArray[index], actual.AsArray[index], $"{path}[{index}]");
            }
            return;
        }

        if (expected.IsBinary)
        {
            CollectionAssert.AreEqual(expected.AsBinary, actual.AsBinary, $"Binary content differs at {path}.");
            return;
        }

        Assert.AreEqual(expected, actual, $"Typed BSON value differs at {path}.");
    }

    private static BsonValue Canonicalize(BsonValue value)
    {
        if (value.IsDocument)
        {
            var result = new BsonDocument();
            foreach (var key in value.AsDocument.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                result[key] = Canonicalize(value.AsDocument[key]);
            }
            return result;
        }

        if (value.IsArray)
        {
            return new BsonArray(value.AsArray.Select(Canonicalize));
        }

        return value;
    }
}
