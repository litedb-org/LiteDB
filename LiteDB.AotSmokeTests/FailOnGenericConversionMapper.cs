using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

#nullable enable
namespace LiteDB.AotSmokeTests;

/// <summary>
/// Turns every route into runtime model mapping into a hard failure, in all publish modes. The broad
/// conversion entry points and the three hooks that every reflection-based object mapping passes through
/// all throw, so a generated collection that quietly fell back would stop the smoke run.
/// </summary>
internal sealed class FailOnGenericConversionMapper : BsonMapper
{
    private const string Trimming = "Runtime model mapping is not trimming safe.";
    private const string DynamicCode = "Runtime type construction requires dynamic code.";

    [RequiresUnreferencedCode(Trimming)]
    public override BsonDocument ToDocument(Type type, object entity) => type == typeof(BsonDocument)
        ? (BsonDocument)entity
        : throw Reached(nameof(ToDocument), type);

    [RequiresUnreferencedCode(Trimming)]
    [RequiresDynamicCode(DynamicCode)]
    public override object ToObject(Type type, BsonDocument document) => type == typeof(BsonDocument)
        ? document
        : throw Reached(nameof(ToObject), type);

    [RequiresUnreferencedCode(Trimming)]
    protected override IEnumerable<MemberInfo> GetTypeMembers(Type type) => throw Reached(nameof(GetTypeMembers), type);

    [RequiresUnreferencedCode(Trimming)]
    protected override BsonDocument SerializeObject(Type type, object obj, int depth) => throw Reached(nameof(SerializeObject), type);

    [RequiresUnreferencedCode(Trimming)]
    [RequiresDynamicCode(DynamicCode)]
    protected override void DeserializeObject(Type type, object obj, BsonDocument value) => throw Reached(nameof(DeserializeObject), type);

    private static InvalidOperationException Reached(string member, Type type) =>
        new($"Generated execution reached runtime model mapping through {member} for '{type}'.");
}
