using LiteDB.SourceGenerator.Utilities;

namespace LiteDB.SourceGenerator.Models;

internal sealed record ModelDescriptor(
    string TypeName,
    EquatableArray<PropertyDescriptor> Properties);
