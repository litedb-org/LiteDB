using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests;

internal sealed class ThrowingConversionMapper : BsonMapper
{
    public override BsonDocument ToDocument(Type type, object entity) =>
        throw new AssertFailedException($"Generated execution must not call {nameof(ToDocument)}.");

    public override object ToObject(Type type, BsonDocument document) =>
        throw new AssertFailedException($"Generated execution must not call {nameof(ToObject)}.");
}
