namespace LiteDB.SourceGenerator.Models;

internal sealed record PropertyDescriptor(
    string Name,
    string Identifier,
    string TypeName,
    string FieldName,
    PropertyKind Kind,
    ScalarConversionKind ScalarKind,
    ScalarConversionKind EnumUnderlyingKind,
    string ScalarTypeName,
    bool IsNullableScalar,
    bool HasBsonId,
    bool IsDeclaringTypeId,
    bool AutoId,
    bool IsId);
