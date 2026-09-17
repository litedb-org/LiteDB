using System;

#nullable enable
namespace LiteDB.AotSmokeTests;

internal sealed class FailOnGenericConversionMapper : BsonMapper
{
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime model mapping is not trimming safe.")]
    public override BsonDocument ToDocument(Type type, object entity) => type == typeof(BsonDocument)
        ? (BsonDocument)entity
        : throw new InvalidOperationException($"Generated execution reached broad {nameof(ToDocument)} conversion for '{type}'.");

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime model mapping is not trimming safe.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime type construction requires dynamic code.")]
    public override object ToObject(Type type, BsonDocument document) => type == typeof(BsonDocument)
        ? document
        : throw new InvalidOperationException($"Generated execution reached broad {nameof(ToObject)} conversion for '{type}'.");
}
