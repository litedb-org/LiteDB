using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LiteDB.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class BsonSourceGenerator : IIncrementalGenerator
{
    private const string SourceGeneratedAttributeName = "LiteDB.BsonSourceGeneratedAttribute";
    private const string BsonIdAttributeName = "LiteDB.BsonIdAttribute";
    private const string BsonFieldAttributeName = "LiteDB.BsonFieldAttribute";
    private const string BsonIgnoreAttributeName = "LiteDB.BsonIgnoreAttribute";

    private static readonly DiagnosticDescriptor UnsupportedModel = new(
        id: "LDBSG001",
        title: "Unsupported source-generated LiteDB model",
        messageFormat: "Type '{0}' cannot use BsonSourceGenerated: {1}",
        category: "LiteDB.SourceGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: SourceGeneratedAttributeName,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (attributeContext, _) => DescribeModel((INamedTypeSymbol)attributeContext.TargetSymbol));

        context.RegisterSourceOutput(models.Collect(), static (productionContext, results) =>
        {
            var validModels = new List<ModelDescriptor>();

            foreach (var result in results)
            {
                if (result.Error is null)
                {
                    validModels.Add(result.Model!);
                }
                else
                {
                    productionContext.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedModel,
                        Location.None,
                        result.TypeName,
                        result.Error));
                }
            }

            if (validModels.Count == 0)
            {
                return;
            }

            validModels.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.TypeName, right.TypeName));
            productionContext.AddSource(
                hintName: "LiteDbGeneratedMappings.g.cs",
                sourceText: SourceText.From(GenerateSource(validModels), Encoding.UTF8));
        });
    }

    private static ModelResult DescribeModel(INamedTypeSymbol type)
    {
        var typeName = GetTypeName(type);

        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType || type.ContainingType is not null)
        {
            return ModelResult.Unsupported(typeName, "it must be a non-abstract, non-generic, top-level class");
        }

        if (type.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal)
        {
            return ModelResult.Unsupported(typeName, "it must be public or internal");
        }

        if (type.InstanceConstructors.Any(static constructor =>
                constructor.Parameters.Length == 0 &&
                (constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)) == false)
        {
            return ModelResult.Unsupported(typeName, "an accessible parameterless constructor is required");
        }

        var hierarchy = new List<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (current.TypeKind != TypeKind.Class || current.IsGenericType || current.ContainingType is not null)
            {
                return ModelResult.Unsupported(typeName, "all base classes must be non-generic, top-level classes");
            }

            if (current.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal)
            {
                return ModelResult.Unsupported(typeName, "all base classes must be public or internal");
            }

            hierarchy.Add(current);
        }

        hierarchy.Reverse();

        var properties = new List<PropertyDescriptor>();
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in hierarchy)
        {
            foreach (var property in level.GetMembers().OfType<IPropertySymbol>().OrderBy(static property => property.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue))
            {
                if (HasAttribute(property, BsonIgnoreAttributeName))
                {
                    continue;
                }

                if (property.IsStatic || property.IsIndexer)
                {
                    return ModelResult.Unsupported(typeName, $"property '{property.Name}' must be a non-static, non-indexed property");
                }

                if (property.DeclaredAccessibility is not Accessibility.Public ||
                    property.GetMethod is null || property.GetMethod.DeclaredAccessibility is not Accessibility.Public ||
                    property.SetMethod is null || property.SetMethod.DeclaredAccessibility is not Accessibility.Public ||
                    property.SetMethod.IsInitOnly)
                {
                    return ModelResult.Unsupported(typeName, $"property '{property.Name}' must have public non-init getter and setter accessors");
                }

                if (memberNames.Add(property.Name) == false)
                {
                    return ModelResult.Unsupported(typeName, $"multiple mapped properties are named '{property.Name}' across the inheritance hierarchy");
                }

                var kind = GetPropertyKind(property.Type);
                if (kind == PropertyKind.Unsupported)
                {
                    return ModelResult.Unsupported(typeName, $"property '{property.Name}' has an unsupported type '{property.Type.ToDisplayString()}'");
                }

                var idAttribute = GetAttribute(property, BsonIdAttributeName);
                var fieldName = GetFieldName(property);
                properties.Add(new PropertyDescriptor(
                    Name: property.Name,
                    Identifier: EscapeIdentifier(property.Name),
                    TypeName: GetTypeName(property.Type),
                    FieldName: fieldName,
                    Kind: kind,
                    HasBsonId: idAttribute is not null,
                    AutoId: GetAutoId(idAttribute),
                    IsId: false));
            }
        }

        if (properties.Count == 0)
        {
            return ModelResult.Unsupported(typeName, "at least one supported property is required");
        }

        var explicitIds = properties.Where(static property => property.HasBsonId).ToArray();
        if (explicitIds.Length > 1)
        {
            return ModelResult.Unsupported(typeName, "multiple properties are marked with BsonId");
        }

        PropertyDescriptor? id = null;
        if (explicitIds.Length == 1)
        {
            id = explicitIds[0];
        }
        else
        {
            var conventionalIds = properties.Where(property =>
                string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(property.Name, type.Name + "Id", StringComparison.OrdinalIgnoreCase)).ToArray();

            if (conventionalIds.Length > 1)
            {
                return ModelResult.Unsupported(typeName, "multiple properties match generated ID conventions across the inheritance hierarchy");
            }

            if (conventionalIds.Length == 1)
            {
                id = conventionalIds[0];
            }
        }

        if (id is not null)
        {
            for (var index = 0; index < properties.Count; index++)
            {
                if (string.Equals(properties[index].Name, id.Name, StringComparison.Ordinal))
                {
                    properties[index] = properties[index] with { IsId = true };
                    break;
                }
            }
        }

        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var fieldName = property.IsId ? "_id" : property.FieldName;
            if (fieldNames.Add(fieldName) == false)
            {
                return ModelResult.Unsupported(typeName, $"multiple mapped properties use BSON field name '{fieldName}' across the inheritance hierarchy");
            }
        }

        return ModelResult.Supported(new ModelDescriptor(typeName, properties.ToImmutableArray()));
    }

    private static PropertyKind GetPropertyKind(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return PropertyKind.Scalar;
        }

        if (type.SpecialType is
            SpecialType.System_Boolean or
            SpecialType.System_Byte or
            SpecialType.System_SByte or
            SpecialType.System_Char or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_Decimal or
            SpecialType.System_String)
        {
            return PropertyKind.Scalar;
        }

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return PropertyKind.Scalar;
        }

        if (type is INamedTypeSymbol namedType)
        {
            var metadataName = namedType.ToDisplayString();
            if (metadataName is "System.DateTime" or "System.Guid" or "LiteDB.ObjectId")
            {
                return PropertyKind.Scalar;
            }

            if (namedType.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>" &&
                namedType.TypeArguments.Length == 1 &&
                namedType.TypeArguments[0].SpecialType == SpecialType.System_String)
            {
                return PropertyKind.StringList;
            }

            if (metadataName == "System.DateTimeOffset")
            {
                return PropertyKind.DateTimeOffset;
            }

            if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                namedType.TypeArguments.Length == 1 &&
                namedType.TypeArguments[0].ToDisplayString() == "System.DateTimeOffset")
            {
                return PropertyKind.NullableDateTimeOffset;
            }
        }

        return PropertyKind.Unsupported;
    }

    private static string GetFieldName(IPropertySymbol property)
    {
        var fieldAttribute = GetAttribute(property, BsonFieldAttributeName);
        if (fieldAttribute is not null)
        {
            if (fieldAttribute.ConstructorArguments.Length > 0 &&
                fieldAttribute.ConstructorArguments[0].Value is string constructorName &&
                string.IsNullOrEmpty(constructorName) == false)
            {
                return constructorName;
            }

            foreach (var namedArgument in fieldAttribute.NamedArguments)
            {
                if (namedArgument.Key == "Name" &&
                    namedArgument.Value.Value is string namedName &&
                    string.IsNullOrEmpty(namedName) == false)
                {
                    return namedName;
                }
            }
        }

        return property.Name;
    }

    private static bool GetAutoId(AttributeData? attribute)
    {
        return attribute?.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is bool autoId
            ? autoId
            : true;
    }

    private static AttributeData? GetAttribute(ISymbol symbol, string metadataName)
    {
        return symbol.GetAttributes().FirstOrDefault(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName)
    {
        return GetAttribute(symbol, metadataName) is not null;
    }

    private static string GenerateSource(IReadOnlyList<ModelDescriptor> models)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine("namespace LiteDB.Generated");
        source.AppendLine("{");
        source.AppendLine("    public static class LiteDbGeneratedMappings");
        source.AppendLine("    {");
        source.AppendLine("        public static void Register(global::LiteDB.BsonMapper mapper)");
        source.AppendLine("        {");
        source.AppendLine("            if (mapper is null) throw new global::System.ArgumentNullException(nameof(mapper));");

        for (var index = 0; index < models.Count; index++)
        {
            source.Append("            var map").Append(index).Append(" = Create").Append(index).AppendLine("();");
        }

        source.AppendLine();
        for (var index = 0; index < models.Count; index++)
        {
            source.Append("            mapper.RegisterGeneratedEntityMapper(map").Append(index).AppendLine(");");
        }

        source.AppendLine("        }");
        AppendStringListHelpers(source);
        if (HasDateTimeOffsetProperties(models))
        {
            AppendDateTimeOffsetHelpers(source);
        }

        for (var index = 0; index < models.Count; index++)
        {
            AppendFactory(source, models[index], index);
        }

        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString();
    }

        private static void AppendDateTimeOffsetHelpers(StringBuilder source)
        {
            source.AppendLine();
            source.AppendLine("        private static global::LiteDB.BsonValue SerializeDateTimeOffset(object? value)");
            source.AppendLine("        {");
            source.AppendLine("            if (value is null) return global::LiteDB.BsonValue.Null;");
            source.AppendLine("            var dateTimeOffset = (global::System.DateTimeOffset)value;");
            source.AppendLine("            return new global::LiteDB.BsonDocument");
            source.AppendLine("            {");
            source.AppendLine("                [\"DateTime\"] = dateTimeOffset.Ticks,");
            source.AppendLine("                [\"Offset\"] = dateTimeOffset.Offset.Ticks");
            source.AppendLine("            };");
            source.AppendLine("        }");
            source.AppendLine();
            source.AppendLine("        private static object DeserializeDateTimeOffset(global::LiteDB.BsonValue value)");
            source.AppendLine("        {");
            source.AppendLine("            if (value.IsNull) return null!;");
            source.AppendLine("            var document = value.AsDocument;");
            source.AppendLine("            return new global::System.DateTimeOffset(");
            source.AppendLine("                document[\"DateTime\"].AsInt64,");
            source.AppendLine("                new global::System.TimeSpan(document[\"Offset\"].AsInt64));");
            source.AppendLine("        }");
        }

        private static void AppendStringListHelpers(StringBuilder source)
        {
        source.AppendLine();
        source.AppendLine("        private static global::LiteDB.BsonValue SerializeStringList(global::System.Collections.Generic.List<string>? values)");
        source.AppendLine("        {");
        source.AppendLine("            if (values is null) return global::LiteDB.BsonValue.Null;");
        source.AppendLine("            var result = new global::LiteDB.BsonArray();");
        source.AppendLine("            foreach (var value in values)");
        source.AppendLine("            {");
        source.AppendLine("                result.Add(value);");
        source.AppendLine("            }");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static global::System.Collections.Generic.List<string>? DeserializeStringList(global::LiteDB.BsonValue value)");
        source.AppendLine("        {");
        source.AppendLine("            if (value.IsNull) return null;");
        source.AppendLine("            var result = new global::System.Collections.Generic.List<string>();");
        source.AppendLine("            foreach (var item in value.AsArray)");
        source.AppendLine("            {");
        source.AppendLine("                result.Add(item.AsString);");
        source.AppendLine("            }");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
    }

        private static bool HasDateTimeOffsetProperties(IReadOnlyList<ModelDescriptor> models)
        {
            return models.Any(static model => model.Properties.Any(static property =>
                property.Kind is PropertyKind.DateTimeOffset or PropertyKind.NullableDateTimeOffset));
        }

        private static void AppendFactory(StringBuilder source, ModelDescriptor model, int index)
        {
        source.AppendLine();
        source.Append("        private static global::LiteDB.EntityMapper Create").Append(index).AppendLine("()");
        source.AppendLine("        {");
        source.Append("            var map = new global::LiteDB.EntityMapper(typeof(").Append(model.TypeName).AppendLine("))");
        source.AppendLine("            {");
        source.Append("                CreateInstance = _ => new ").Append(model.TypeName).AppendLine("()");
        source.AppendLine("            };");

        foreach (var property in model.Properties)
        {
            source.AppendLine();
            source.AppendLine("            map.Members.Add(new global::LiteDB.MemberMapper");
            source.AppendLine("            {");
            source.Append("                AutoId = ").Append(property.IsId && property.AutoId ? "true" : "false").AppendLine(",");
            source.Append("                FieldName = ").Append(SymbolDisplay.FormatLiteral(property.IsId ? "_id" : property.FieldName, true)).AppendLine(",");
            source.Append("                MemberName = ").Append(SymbolDisplay.FormatLiteral(property.Name, true)).AppendLine(",");
            source.Append("                DataType = typeof(").Append(property.TypeName).AppendLine("),");
            source.Append("                UnderlyingType = typeof(").Append(property.Kind == PropertyKind.StringList ? "global::System.String" : property.TypeName).AppendLine("),");
            source.Append("                IsEnumerable = ").Append(property.Kind == PropertyKind.StringList ? "true" : "false").AppendLine(",");

            if (property.Kind == PropertyKind.StringList)
            {
                source.Append("                Serialize = (value, _) => SerializeStringList((global::System.Collections.Generic.List<string>)value),").AppendLine();
                source.Append("                Deserialize = (value, _) => DeserializeStringList(value)").AppendLine(",");
            }
            else if (property.Kind is PropertyKind.DateTimeOffset or PropertyKind.NullableDateTimeOffset)
            {
                source.Append("                Serialize = (value, _) => SerializeDateTimeOffset(value),").AppendLine();
                source.Append("                Deserialize = (value, _) => DeserializeDateTimeOffset(value)").AppendLine(",");
            }

            source.Append("                Getter = entity => ((").Append(model.TypeName).Append(")entity).").Append(property.Identifier).AppendLine(",");
            source.Append("                Setter = (entity, value) => ((").Append(model.TypeName).Append(")entity).").Append(property.Identifier).Append(" = (").Append(property.TypeName).AppendLine(")value");
            source.AppendLine("            });");
        }

        source.AppendLine();
        source.AppendLine("            return map;");
        source.AppendLine("        }");
    }

    private static string EscapeIdentifier(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;
    }

    private static string GetTypeName(ITypeSymbol symbol)
    {
        return symbol.WithNullableAnnotation(NullableAnnotation.None)
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers));
    }

    private sealed record ModelResult(ModelDescriptor? Model, string TypeName, string? Error)
    {
        public static ModelResult Supported(ModelDescriptor model) => new(model, model.TypeName, null);
        public static ModelResult Unsupported(string typeName, string error) => new(null, typeName, error);
    }

    private sealed record ModelDescriptor(string TypeName, ImmutableArray<PropertyDescriptor> Properties);

    private sealed record PropertyDescriptor(
        string Name,
        string Identifier,
        string TypeName,
        string FieldName,
        PropertyKind Kind,
        bool HasBsonId,
        bool AutoId,
        bool IsId);

    private enum PropertyKind
    {
        Scalar,
        StringList,
        DateTimeOffset,
        NullableDateTimeOffset,
        Unsupported
    }
}
