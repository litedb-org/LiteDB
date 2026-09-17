namespace LiteDB.SourceGenerator.Models;

internal enum PropertyKind
{
    Scalar,
    StringList,
    StringArray,
    DynamicDictionary,
    DateTimeOffset,
    NullableDateTimeOffset,
    Unsupported
}
