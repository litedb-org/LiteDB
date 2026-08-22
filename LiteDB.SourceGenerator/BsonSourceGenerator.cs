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

        private static readonly DiagnosticDescriptor InvalidModel = new(
            id: "LDBSG001",
            title: "Invalid source-generated model",
            messageFormat: "Type '{0}' cannot use BsonSourceGenerated: {1}",
            category: "LiteDB.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor InvalidProperty = new(
            id: "LDBSG002",
            title: "Invalid source-generated property",
            messageFormat: "Type '{0}' cannot use BsonSourceGenerated: {1}",
            category: "LiteDB.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor MappingConflict = new(
            id: "LDBSG003",
            title: "Conflicting source-generated mapping",
            messageFormat: "Type '{0}' cannot use BsonSourceGenerated: {1}",
            category: "LiteDB.SourceGenerator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: SourceGeneratedAttributeName,
            predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
            transform: static (attributeContext, _) => DescribeModel(
                (INamedTypeSymbol)attributeContext.TargetSymbol,
                GetDiagnosticLocation(attributeContext.TargetNode)));

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
                        GetDiagnosticDescriptor(result.DiagnosticKind),
                        result.DiagnosticLocation!.Create(),
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

    private static DiagnosticDescriptor GetDiagnosticDescriptor(DiagnosticKind kind) => kind switch
    {
        DiagnosticKind.InvalidModel => InvalidModel,
        DiagnosticKind.InvalidProperty => InvalidProperty,
        DiagnosticKind.MappingConflict => MappingConflict,
        _ => throw new InvalidOperationException($"Unsupported diagnostic kind '{kind}'.")
    };

    private static ModelResult DescribeModel(INamedTypeSymbol type, DiagnosticLocationDescriptor diagnosticLocation)
    {
        var typeName = GetTypeName(type);

        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType || type.ContainingType is not null)
        {
            return ModelResult.InvalidModel(typeName, diagnosticLocation, "it must be a non-abstract, non-generic, top-level class");
        }

        if (type.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal)
        {
            return ModelResult.InvalidModel(typeName, diagnosticLocation, "it must be public or internal");
        }

        if (!type.InstanceConstructors.Any(static constructor =>
                constructor.Parameters.Length == 0 &&
                (constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)))
        {
            return ModelResult.InvalidModel(typeName, diagnosticLocation, "an accessible parameterless constructor is required");
        }

        var hierarchy = new List<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (current.TypeKind != TypeKind.Class || current.IsGenericType || current.ContainingType is not null)
            {
                return ModelResult.InvalidModel(typeName, diagnosticLocation, "all base classes must be non-generic, top-level classes");
            }

            if (current.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal)
            {
                return ModelResult.InvalidModel(typeName, diagnosticLocation, "all base classes must be public or internal");
            }

            hierarchy.Add(current);
        }

        hierarchy.Reverse();

        var properties = new List<PropertyDescriptor>();
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in GetSelectedProperties(hierarchy))
        {
            if (HasAttribute(property, BsonIgnoreAttributeName))
            {
                continue;
            }

            if (property.IsStatic || property.IsIndexer)
            {
                return ModelResult.InvalidProperty(typeName, diagnosticLocation, $"property '{property.Name}' must be a non-static, non-indexed property");
            }

            if (IsComputedProperty(property, type))
            {
                continue;
            }

            if (property.DeclaredAccessibility is not Accessibility.Public ||
                property.GetMethod is null || property.GetMethod.DeclaredAccessibility is not Accessibility.Public ||
                property.SetMethod is null || property.SetMethod.DeclaredAccessibility is not Accessibility.Public ||
                property.SetMethod.IsInitOnly)
            {
                return ModelResult.InvalidProperty(typeName, diagnosticLocation, $"property '{property.Name}' must have public non-init getter and setter accessors");
            }

            if (!memberNames.Add(property.Name))
            {
                return ModelResult.MappingConflict(typeName, diagnosticLocation, $"multiple mapped properties are named '{property.Name}' across the inheritance hierarchy");
            }

            var kind = GetPropertyKind(property.Type);
            if (kind == PropertyKind.Unsupported)
            {
                return ModelResult.InvalidProperty(typeName, diagnosticLocation, $"property '{property.Name}' has an unsupported type '{property.Type.ToDisplayString()}'");
            }

            var scalarKind = GetScalarConversionKind(property.Type, out var isNullableScalar);
            var idAttribute = GetAttribute(property, BsonIdAttributeName);
            var fieldName = GetFieldName(property);
            properties.Add(new PropertyDescriptor(
                Name: property.Name,
                Identifier: EscapeIdentifier(property.Name),
                TypeName: GetTypeName(property.Type),
                FieldName: fieldName,
                Kind: kind,
                ScalarKind: scalarKind,
                ScalarTypeName: GetScalarTypeName(property.Type),
                IsNullableScalar: isNullableScalar,
                HasBsonId: idAttribute is not null,
                AutoId: GetAutoId(idAttribute),
                IsId: false));
        }

        if (properties.Count == 0)
        {
            return ModelResult.InvalidModel(typeName, diagnosticLocation, "at least one supported property is required");
        }

        var explicitIds = properties.Where(static property => property.HasBsonId).ToArray();
        if (explicitIds.Length > 1)
        {
            return ModelResult.MappingConflict(typeName, diagnosticLocation, "multiple properties are marked with BsonId");
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
                return ModelResult.MappingConflict(typeName, diagnosticLocation, "multiple properties match generated ID conventions across the inheritance hierarchy");
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
                    properties[index] = properties[index] with { FieldName = "_id", IsId = true };
                    break;
                }
            }
        }

        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (!fieldNames.Add(property.FieldName))
            {
                return ModelResult.MappingConflict(typeName, diagnosticLocation, $"multiple mapped properties use BSON field name '{property.FieldName}' across the inheritance hierarchy");
            }
        }

        return ModelResult.Supported(new ModelDescriptor(
            typeName,
            properties.ToImmutableArray(),
            CanEmitExecutionMap(properties, hierarchy.Count != 1)));
    }

    private static PropertyKind GetPropertyKind(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return PropertyKind.Scalar;
        }

        if (type is IArrayTypeSymbol arrayType &&
            arrayType.Rank == 1 &&
            arrayType.ElementType.SpecialType == SpecialType.System_String)
        {
            return PropertyKind.StringArray;
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
            if (IsStringObjectDictionary(namedType))
            {
                return PropertyKind.DynamicDictionary;
            }

            if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                namedType.TypeArguments.Length == 1)
            {
                var underlyingKind = GetPropertyKind(namedType.TypeArguments[0]);
                return underlyingKind switch
                {
                    PropertyKind.Scalar => PropertyKind.Scalar,
                    PropertyKind.DateTimeOffset => PropertyKind.NullableDateTimeOffset,
                    _ => PropertyKind.Unsupported
                };
            }

            var metadataName = namedType.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();
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
        }

        return PropertyKind.Unsupported;
    }

    private static IReadOnlyList<IPropertySymbol> GetSelectedProperties(IReadOnlyList<INamedTypeSymbol> hierarchy)
    {
        var overriddenAncestors = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var level in hierarchy)
        {
            foreach (var property in level.GetMembers().OfType<IPropertySymbol>())
            {
                for (var overridden = property.OverriddenProperty; overridden is not null; overridden = overridden.OverriddenProperty)
                {
                    overriddenAncestors.Add(overridden);
                }
            }
        }

        var selected = new List<IPropertySymbol>();
        foreach (var level in hierarchy)
        {
            foreach (var property in level.GetMembers().OfType<IPropertySymbol>().OrderBy(static property => property.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue))
            {
                if (property.IsImplicitlyDeclared == false && overriddenAncestors.Contains(property) == false)
                {
                    selected.Add(property);
                }
            }
        }

        return selected;
    }

    private static DiagnosticLocationDescriptor GetDiagnosticLocation(SyntaxNode targetNode)
    {
        var location = targetNode is TypeDeclarationSyntax typeDeclaration
            ? typeDeclaration.Identifier.GetLocation()
            : targetNode.GetLocation();
        var lineSpan = location.GetLineSpan();

        return new DiagnosticLocationDescriptor(lineSpan.Path, location.SourceSpan, lineSpan.Span);
    }

    private static string GetFieldName(IPropertySymbol property)
    {
        var fieldAttribute = GetAttribute(property, BsonFieldAttributeName);
        if (fieldAttribute is not null)
        {
            if (fieldAttribute.ConstructorArguments.Length > 0 &&
                fieldAttribute.ConstructorArguments[0].Value is string constructorName &&
                !string.IsNullOrEmpty(constructorName))
            {
                return constructorName;
            }

            foreach (var namedArgument in fieldAttribute.NamedArguments)
            {
                if (namedArgument.Key == "Name" &&
                    namedArgument.Value.Value is string namedName &&
                    !string.IsNullOrEmpty(namedName))
                {
                    return namedName;
                }
            }
        }

        return property.Name;
    }

    private static bool IsStringObjectDictionary(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        return definition.MetadataName == "Dictionary`2" &&
            definition.ContainingNamespace.ToDisplayString() == "System.Collections.Generic" &&
            type.TypeArguments.Length == 2 &&
            type.TypeArguments[0].SpecialType == SpecialType.System_String &&
            type.TypeArguments[1].SpecialType == SpecialType.System_Object;
    }

    private static bool IsComputedProperty(IPropertySymbol property, INamedTypeSymbol modelType)
    {
        if (property.GetMethod?.DeclaredAccessibility != Accessibility.Public || property.SetMethod is not null)
        {
            return false;
        }

        if (HasAttribute(property, BsonIdAttributeName) || HasAttribute(property, BsonFieldAttributeName))
        {
            return false;
        }

        return !string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(property.Name, modelType.Name + "Id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetAutoId(AttributeData? attribute)
    {
        return attribute?.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not bool autoId
            || autoId;
    }

    private static AttributeData? GetAttribute(IPropertySymbol property, string metadataName)
    {
        for (var current = property; current is not null; current = current.OverriddenProperty)
        {
            var attribute = current.GetAttributes().FirstOrDefault(candidate =>
                string.Equals(candidate.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));
            if (attribute is not null)
            {
                return attribute;
            }
        }

        return null;
    }

    private static bool HasAttribute(IPropertySymbol property, string metadataName)
    {
        return GetAttribute(property, metadataName) is not null;
    }

    private static string GetScalarTypeName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullableType)
        {
            type = nullableType.TypeArguments[0];
        }

        return GetTypeName(type);
    }

    private static ScalarConversionKind GetScalarConversionKind(ITypeSymbol type, out bool isNullableScalar)
    {
        isNullableScalar = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullableType)
        {
            isNullableScalar = true;
            type = nullableType.TypeArguments[0];
        }

        if (type.TypeKind == TypeKind.Enum) return ScalarConversionKind.Enum;

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte }) return ScalarConversionKind.ByteArray;

        var specialType = type.SpecialType;
        if (specialType == SpecialType.System_Boolean) return ScalarConversionKind.Boolean;
        if (specialType == SpecialType.System_Byte) return ScalarConversionKind.Byte;
        if (specialType == SpecialType.System_SByte) return ScalarConversionKind.SByte;
        if (specialType == SpecialType.System_Char) return ScalarConversionKind.Char;
        if (specialType == SpecialType.System_Int16) return ScalarConversionKind.Int16;
        if (specialType == SpecialType.System_UInt16) return ScalarConversionKind.UInt16;
        if (specialType == SpecialType.System_Int32) return ScalarConversionKind.Int32;
        if (specialType == SpecialType.System_UInt32) return ScalarConversionKind.UInt32;
        if (specialType == SpecialType.System_Int64) return ScalarConversionKind.Int64;
        if (specialType == SpecialType.System_UInt64) return ScalarConversionKind.UInt64;
        if (specialType == SpecialType.System_Single) return ScalarConversionKind.Single;
        if (specialType == SpecialType.System_Double) return ScalarConversionKind.Double;
        if (specialType == SpecialType.System_Decimal) return ScalarConversionKind.Decimal;
        if (specialType == SpecialType.System_String) return ScalarConversionKind.String;

        return type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() switch
        {
            "System.DateTime" => ScalarConversionKind.DateTime,
            "System.Guid" => ScalarConversionKind.Guid,
            "LiteDB.ObjectId" => ScalarConversionKind.ObjectId,
            _ => ScalarConversionKind.None
        };
    }

    private static bool CanEmitExecutionMap(IReadOnlyList<PropertyDescriptor> properties, bool hasInheritance)
    {
        return hasInheritance == false &&
            properties.All(property =>
                (property.Kind == PropertyKind.Scalar && property.ScalarKind != ScalarConversionKind.None) ||
                property.Kind is PropertyKind.StringList or PropertyKind.StringArray);
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

        for (var index = 0; index < models.Count; index++)
        {
            if (models[index].CanEmitExecutionMap)
            {
                source.Append("            mapper.RegisterGeneratedExecutionMap(CreateExecutionMap").Append(index).AppendLine("());");
            }
        }

        source.AppendLine("        }");
        AppendStringListHelpers(source);
        if (HasStringArrayProperties(models))
        {
            AppendStringArrayHelpers(source);
        }

        if (HasDynamicDictionaryProperties(models))
        {
            AppendDynamicDictionaryHelpers(source);
        }

        if (HasDateTimeOffsetProperties(models))
        {
            AppendDateTimeOffsetHelpers(source);
        }

        for (var index = 0; index < models.Count; index++)
        {
            AppendFactory(source, models[index], index);
            if (models[index].CanEmitExecutionMap)
            {
                AppendExecutionMapFactory(source, models[index], index);
            }
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
        source.AppendLine("            if (value.IsDateTime)");
        source.AppendLine("            {");
        source.AppendLine("                return new global::System.DateTimeOffset(value.AsDateTime.ToUniversalTime());");
        source.AppendLine("            }");
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

    private static void AppendStringArrayHelpers(StringBuilder source)
    {
        source.AppendLine();
        source.AppendLine("        private static global::LiteDB.BsonValue SerializeStringArray(string[]? values)");
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
        source.AppendLine("        private static string[]? DeserializeStringArray(global::LiteDB.BsonValue value)");
        source.AppendLine("        {");
        source.AppendLine("            if (value.IsNull) return null;");
        source.AppendLine("            var array = value.AsArray;");
        source.AppendLine("            var result = new string[array.Count];");
        source.AppendLine("            for (var index = 0; index < array.Count; index++)");
        source.AppendLine("            {");
        source.AppendLine("                result[index] = array[index].AsString;");
        source.AppendLine("            }");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
    }

    private static void AppendDynamicDictionaryHelpers(StringBuilder source)
    {
        source.AppendLine();
        source.AppendLine("        private static global::LiteDB.BsonValue SerializeDynamicDictionary(global::System.Collections.Generic.Dictionary<string, object?>? values)");
        source.AppendLine("        {");
        source.AppendLine("            if (values is null) return global::LiteDB.BsonValue.Null;");
        source.AppendLine("            var result = new global::LiteDB.BsonDocument();");
        source.AppendLine("            foreach (var pair in values)");
        source.AppendLine("            {");
        source.AppendLine("                result[pair.Key] = SerializeDynamicValue(pair.Value);");
        source.AppendLine("            }");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static global::LiteDB.BsonValue SerializeDynamicValue(object? value)");
        source.AppendLine("        {");
        source.AppendLine("            if (value is null) return global::LiteDB.BsonValue.Null;");
        source.AppendLine("            return value switch");
        source.AppendLine("            {");
        source.AppendLine("                global::LiteDB.BsonValue bsonValue => bsonValue,");
        source.AppendLine("                int integer => new global::LiteDB.BsonValue(integer),");
        source.AppendLine("                long integer => new global::LiteDB.BsonValue(integer),");
        source.AppendLine("                double number => new global::LiteDB.BsonValue(number),");
        source.AppendLine("                decimal number => new global::LiteDB.BsonValue(number),");
        source.AppendLine("                string text => new global::LiteDB.BsonValue(text),");
        source.AppendLine("                byte[] bytes => new global::LiteDB.BsonValue(bytes),");
        source.AppendLine("                global::LiteDB.ObjectId objectId => new global::LiteDB.BsonValue(objectId),");
        source.AppendLine("                global::System.Guid guid => new global::LiteDB.BsonValue(guid),");
        source.AppendLine("                bool boolean => new global::LiteDB.BsonValue(boolean),");
        source.AppendLine("                global::System.DateTime dateTime => new global::LiteDB.BsonValue(dateTime),");
        source.AppendLine("                short integer => new global::LiteDB.BsonValue((int)integer),");
        source.AppendLine("                ushort integer => new global::LiteDB.BsonValue((int)integer),");
        source.AppendLine("                byte integer => new global::LiteDB.BsonValue((int)integer),");
        source.AppendLine("                sbyte integer => new global::LiteDB.BsonValue((int)integer),");
        source.AppendLine("                uint integer => new global::LiteDB.BsonValue((long)integer),");
        source.AppendLine("                ulong integer => new global::LiteDB.BsonValue(unchecked((long)integer)),");
        source.AppendLine("                float number => new global::LiteDB.BsonValue((double)number),");
        source.AppendLine("                char character => new global::LiteDB.BsonValue(character.ToString()),");
        source.AppendLine("                global::System.Enum enumeration => new global::LiteDB.BsonValue(enumeration.ToString()),");
        source.AppendLine("                global::System.Collections.IDictionary dictionary => SerializeDynamicDocument(dictionary),");
        source.AppendLine("                global::System.Collections.IEnumerable values => SerializeDynamicArray(values),");
        source.AppendLine("                _ => throw new global::System.InvalidOperationException($\"Unsupported dynamic dictionary value type '{value.GetType().FullName}'. Use BSON-native values, nested dictionaries, or nested enumerables.\")");
        source.AppendLine("            };");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static global::LiteDB.BsonValue SerializeDynamicDocument(global::System.Collections.IDictionary values)");
        source.AppendLine("        {");
        source.AppendLine("            var result = new global::LiteDB.BsonDocument();");
        source.AppendLine("            foreach (global::System.Collections.DictionaryEntry entry in values)");
        source.AppendLine("            {");
        source.AppendLine("                result[entry.Key?.ToString() ?? string.Empty] = SerializeDynamicValue(entry.Value);");
        source.AppendLine("            }");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static global::LiteDB.BsonValue SerializeDynamicArray(global::System.Collections.IEnumerable values)");
        source.AppendLine("        {");
        source.AppendLine("            var result = new global::LiteDB.BsonArray();");
        source.AppendLine("            foreach (var value in values)");
        source.AppendLine("            {");
        source.AppendLine("                result.Add(SerializeDynamicValue(value));");
        source.AppendLine("            }");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static global::System.Collections.Generic.Dictionary<string, object?>? DeserializeDynamicDictionary(global::LiteDB.BsonValue value)");
        source.AppendLine("        {");
        source.AppendLine("            return value.IsNull ? null : DeserializeDynamicDocument(value.AsDocument);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static global::System.Collections.Generic.Dictionary<string, object?> DeserializeDynamicDocument(global::LiteDB.BsonDocument values)");
        source.AppendLine("        {");
        source.AppendLine("            var result = new global::System.Collections.Generic.Dictionary<string, object?>();");
        source.AppendLine("            foreach (var pair in values.GetElements())");
        source.AppendLine("            {");
        source.AppendLine("                result[pair.Key] = DeserializeDynamicValue(pair.Value);");
        source.AppendLine("            }");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static object?[] DeserializeDynamicArray(global::LiteDB.BsonArray values)");
        source.AppendLine("        {");
        source.AppendLine("            var result = new object?[values.Count];");
        source.AppendLine("            for (var index = 0; index < values.Count; index++)");
        source.AppendLine("            {");
        source.AppendLine("                result[index] = DeserializeDynamicValue(values[index]);");
        source.AppendLine("            }");
        source.AppendLine("            return result;");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        private static object? DeserializeDynamicValue(global::LiteDB.BsonValue value)");
        source.AppendLine("        {");
        source.AppendLine("            if (value.IsNull) return null;");
        source.AppendLine("            return value.Type switch");
        source.AppendLine("            {");
        source.AppendLine("                global::LiteDB.BsonType.Document => DeserializeDynamicDocument(value.AsDocument),");
        source.AppendLine("                global::LiteDB.BsonType.Array => DeserializeDynamicArray(value.AsArray),");
        source.AppendLine("                _ => value.RawValue");
        source.AppendLine("            };");
        source.AppendLine("        }");
    }

    private static bool HasDynamicDictionaryProperties(IReadOnlyList<ModelDescriptor> models)
    {
        return models.Any(static model => model.Properties.Any(static property => property.Kind == PropertyKind.DynamicDictionary));
    }

    private static bool HasStringArrayProperties(IReadOnlyList<ModelDescriptor> models)
    {
        return models.Any(static model => model.Properties.Any(static property => property.Kind == PropertyKind.StringArray));
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
            source.Append("                FieldName = ").Append(SymbolDisplay.FormatLiteral(property.FieldName, true)).AppendLine(",");
            source.Append("                MemberName = ").Append(SymbolDisplay.FormatLiteral(property.Name, true)).AppendLine(",");
            source.Append("                DataType = typeof(").Append(property.TypeName).AppendLine("),");
            source.Append("                UnderlyingType = typeof(").Append(property.Kind is PropertyKind.StringList or PropertyKind.StringArray ? "global::System.String" : property.TypeName).AppendLine("),");
            source.Append("                IsEnumerable = ").Append(property.Kind is PropertyKind.StringList or PropertyKind.StringArray ? "true" : "false").AppendLine(",");

            if (property.Kind == PropertyKind.StringList)
            {
                source.AppendLine("                Serialize = (value, _) => SerializeStringList((global::System.Collections.Generic.List<string>)value),");
                source.Append("                Deserialize = (value, _) => DeserializeStringList(value)").AppendLine(",");
            }
            else if (property.Kind == PropertyKind.StringArray)
            {
                source.AppendLine("                Serialize = (value, _) => SerializeStringArray((string[])value),");
                source.Append("                Deserialize = (value, _) => DeserializeStringArray(value)").AppendLine(",");
            }
            else if (property.Kind == PropertyKind.DynamicDictionary)
            {
                source.AppendLine("                Serialize = (value, _) => SerializeDynamicDictionary((global::System.Collections.Generic.Dictionary<string, object?>)value),");
                source.Append("                Deserialize = (value, _) => DeserializeDynamicDictionary(value)").AppendLine(",");
            }
            else if (property.Kind is PropertyKind.DateTimeOffset or PropertyKind.NullableDateTimeOffset)
            {
                source.AppendLine("                Serialize = (value, _) => SerializeDateTimeOffset(value),");
                source.Append("                Deserialize = (value, _) => DeserializeDateTimeOffset(value)").AppendLine(",");
            }

            source.Append("                Getter = entity => ((").Append(model.TypeName).Append(")entity).").Append(property.Identifier).AppendLine(",");
            source.Append("                Setter = (entity, value) => ((").Append(model.TypeName).Append(")entity).").Append(property.Identifier).Append(" = (").Append(property.Kind == PropertyKind.DynamicDictionary ? "global::System.Collections.Generic.Dictionary<string, object?>" : property.TypeName).AppendLine(")value");
            source.AppendLine("            });");
        }

        source.AppendLine();
        source.AppendLine("            return map;");
        source.AppendLine("        }");
    }

    private static void AppendExecutionMapFactory(StringBuilder source, ModelDescriptor model, int index)
    {
        source.AppendLine();
        source.Append("        private static global::LiteDB.GeneratedEntityMap<").Append(model.TypeName).Append("> CreateExecutionMap").Append(index).AppendLine("()");
        source.AppendLine("        {");
        source.Append("            return new global::LiteDB.GeneratedEntityMap<").Append(model.TypeName).Append(">(SerializeExecution").Append(index).Append(", DeserializeExecution").Append(index).AppendLine(");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        private static global::LiteDB.BsonDocument SerializeExecution").Append(index).Append("(").Append(model.TypeName).AppendLine(" entity, global::LiteDB.GeneratedExecutionOptions options)");
        source.AppendLine("        {");
        source.AppendLine("            var document = new global::LiteDB.BsonDocument();");

        foreach (var property in model.Properties)
        {
            AppendSerializeExecutionProperty(source, property, SymbolDisplay.FormatLiteral(property.FieldName, true));
        }

        source.AppendLine("            return document;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        private static ").Append(model.TypeName).Append(" DeserializeExecution").Append(index).Append("(global::LiteDB.BsonDocument document, global::LiteDB.GeneratedExecutionOptions _)").AppendLine();
        source.AppendLine("        {");
        source.Append("            var entity = new ").Append(model.TypeName).AppendLine("();");

        for (var propertyIndex = 0; propertyIndex < model.Properties.Length; propertyIndex++)
        {
            var property = model.Properties[propertyIndex];
            AppendDeserializeExecutionProperty(source, property, SymbolDisplay.FormatLiteral(property.FieldName, true), propertyIndex);
        }

        source.AppendLine("            return entity;");
        source.AppendLine("        }");
    }

    private static void AppendSerializeExecutionProperty(StringBuilder source, PropertyDescriptor property, string fieldLiteral)
    {
        var access = "entity." + property.Identifier;
        if (property.Kind is PropertyKind.StringList or PropertyKind.StringArray)
        {
            source.Append("            if (").Append(access).AppendLine(" is null)");
            source.AppendLine("            {");
            source.Append("                if (options.SerializeNullValues) document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            source.AppendLine("            }");
            source.AppendLine("            else");
            source.AppendLine("            {");
            source.AppendLine("                var array = new global::LiteDB.BsonArray();");
            source.Append("                foreach (var item in ").Append(access).AppendLine(")");
            source.AppendLine("                {");
            source.AppendLine("                    if (item is null)");
            source.AppendLine("                    {");
            source.AppendLine("                        array.Add(global::LiteDB.BsonValue.Null);");
            source.AppendLine("                    }");
            source.AppendLine("                    else");
            source.AppendLine("                    {");
            source.AppendLine("                        var text = options.TrimWhitespace ? item.Trim() : item;");
            source.AppendLine("                        array.Add(options.EmptyStringToNull && text.Length == 0 ? global::LiteDB.BsonValue.Null : new global::LiteDB.BsonValue(text));");
            source.AppendLine("                    }");
            source.AppendLine("                }");
            source.Append("                document[").Append(fieldLiteral).AppendLine("] = array;");
            source.AppendLine("            }");
            return;
        }

        if (property.ScalarKind == ScalarConversionKind.String)
        {
            source.Append("            if (").Append(access).AppendLine(" is null)");
            source.AppendLine("            {");
            if (property.IsId)
            {
                source.Append("                document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            }
            else
            {
                source.Append("                if (options.SerializeNullValues) document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            }
            source.AppendLine("            }");
            source.AppendLine("            else");
            source.AppendLine("            {");
            source.Append("                var text = options.TrimWhitespace ? ").Append(access).Append(".Trim() : ").Append(access).AppendLine(";");
            source.Append("                document[").Append(fieldLiteral).Append("] = options.EmptyStringToNull && text.Length == 0 ? global::LiteDB.BsonValue.Null : new global::LiteDB.BsonValue(text);").AppendLine();
            source.AppendLine("            }");
            return;
        }

        if (property.IsNullableScalar || IsReferenceScalar(property.ScalarKind))
        {
            source.Append("            if (").Append(access).AppendLine(" is null)");
            source.AppendLine("            {");
            if (property.IsId)
            {
                source.Append("                document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            }
            else
            {
                source.Append("                if (options.SerializeNullValues) document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            }
            source.AppendLine("            }");
            source.AppendLine("            else");
            source.AppendLine("            {");
            var value = property.IsNullableScalar ? access + ".Value" : access;
            source.Append("                document[").Append(fieldLiteral).Append("] = ").Append(GetSerializeExpression(property, value)).AppendLine(";");
            source.AppendLine("            }");
            return;
        }

        source.Append("            document[").Append(fieldLiteral).Append("] = ").Append(GetSerializeExpression(property, access)).AppendLine(";");
    }

    private static void AppendDeserializeExecutionProperty(StringBuilder source, PropertyDescriptor property, string fieldLiteral, int propertyIndex)
    {
        var value = "value" + propertyIndex;
        source.Append("            if (document.TryGetValue(").Append(fieldLiteral).Append(", out var ").Append(value).AppendLine(") && " + value + ".IsNull == false)");
        source.AppendLine("            {");
        if (property.Kind == PropertyKind.StringList)
        {
            source.Append("                var result").Append(propertyIndex).Append(" = new global::System.Collections.Generic.List<string>(").Append(value).AppendLine(".AsArray.Count);");
            source.Append("                foreach (var item in ").Append(value).AppendLine(".AsArray)");
            source.AppendLine("                {");
            source.Append("                    result").Append(propertyIndex).AppendLine(".Add(item.IsNull ? null! : item.AsString);");
            source.AppendLine("                }");
            source.Append("                entity.").Append(property.Identifier).Append(" = result").Append(propertyIndex).AppendLine(";");
        }
        else if (property.Kind == PropertyKind.StringArray)
        {
            source.Append("                var array").Append(propertyIndex).Append(" = ").Append(value).AppendLine(".AsArray;");
            source.Append("                var result").Append(propertyIndex).Append(" = new string[array").Append(propertyIndex).AppendLine(".Count];");
            source.Append("                for (var index = 0; index < array").Append(propertyIndex).AppendLine(".Count; index++)");
            source.AppendLine("                {");
            source.Append("                    var item = array").Append(propertyIndex).AppendLine("[index];");
            source.Append("                    result").Append(propertyIndex).AppendLine("[index] = item.IsNull ? null! : item.AsString;");
            source.AppendLine("                }");
            source.Append("                entity.").Append(property.Identifier).Append(" = result").Append(propertyIndex).AppendLine(";");
        }
        else
        {
            source.Append("                entity.").Append(property.Identifier).Append(" = ").Append(GetDeserializeExpression(property, value)).AppendLine(";");
        }
        source.AppendLine("            }");
        if (property.IsNullableScalar || IsReferenceScalar(property.ScalarKind) ||
            property.Kind is PropertyKind.StringList or PropertyKind.StringArray)
        {
            source.Append("            else if (document.TryGetValue(").Append(fieldLiteral).Append(", out ").Append(value).Append(") && ").Append(value).AppendLine(".IsNull)");
            source.AppendLine("            {");
            source.Append("                entity.").Append(property.Identifier).AppendLine(" = null!;");
            source.AppendLine("            }");
        }
    }

    private static bool IsReferenceScalar(ScalarConversionKind kind)
    {
        return kind is ScalarConversionKind.String or ScalarConversionKind.ByteArray or ScalarConversionKind.ObjectId;
    }

    private static string GetSerializeExpression(PropertyDescriptor property, string value)
    {
        return property.ScalarKind switch
        {
            ScalarConversionKind.Boolean => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.Byte => "new global::LiteDB.BsonValue((int)" + value + ")",
            ScalarConversionKind.SByte => "new global::LiteDB.BsonValue((int)" + value + ")",
            ScalarConversionKind.Char => "new global::LiteDB.BsonValue(" + value + ".ToString())",
            ScalarConversionKind.Int16 => "new global::LiteDB.BsonValue((int)" + value + ")",
            ScalarConversionKind.UInt16 => "new global::LiteDB.BsonValue((int)" + value + ")",
            ScalarConversionKind.Int32 => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.UInt32 => "new global::LiteDB.BsonValue((long)" + value + ")",
            ScalarConversionKind.Int64 => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.UInt64 => "new global::LiteDB.BsonValue(unchecked((long)" + value + "))",
            ScalarConversionKind.Single => "new global::LiteDB.BsonValue((double)" + value + ")",
            ScalarConversionKind.Double => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.Decimal => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.ByteArray => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.DateTime => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.Guid => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.ObjectId => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.Enum => "options.EnumAsInteger ? new global::LiteDB.BsonValue((int)" + value + ") : new global::LiteDB.BsonValue(" + value + ".ToString())",
            _ => throw new InvalidOperationException("Unsupported generated execution scalar conversion.")
        };
    }

    private static string GetDeserializeExpression(PropertyDescriptor property, string value)
    {
        return property.ScalarKind switch
        {
            ScalarConversionKind.Boolean => value + ".AsBoolean",
            ScalarConversionKind.Byte => "(byte)" + value + ".AsInt32",
            ScalarConversionKind.SByte => "(sbyte)" + value + ".AsInt32",
            ScalarConversionKind.Char => value + ".AsString[0]",
            ScalarConversionKind.Int16 => "(short)" + value + ".AsInt32",
            ScalarConversionKind.UInt16 => "(ushort)" + value + ".AsInt32",
            ScalarConversionKind.Int32 => value + ".AsInt32",
            ScalarConversionKind.UInt32 => "(uint)" + value + ".AsInt64",
            ScalarConversionKind.Int64 => value + ".AsInt64",
            ScalarConversionKind.UInt64 => "unchecked((global::System.UInt64)" + value + ".AsInt64)",
            ScalarConversionKind.Single => "(float)" + value + ".AsDouble",
            ScalarConversionKind.Double => value + ".AsDouble",
            ScalarConversionKind.Decimal => value + ".AsDecimal",
            ScalarConversionKind.String => value + ".AsString",
            ScalarConversionKind.ByteArray => value + ".AsBinary",
            ScalarConversionKind.DateTime => value + ".AsDateTime",
            ScalarConversionKind.Guid => value + ".AsGuid",
            ScalarConversionKind.ObjectId => value + ".AsObjectId",
            ScalarConversionKind.Enum => value + ".IsInt32 ? (" + property.ScalarTypeName + ")" + value + ".AsInt32 : global::System.Enum.Parse<" + property.ScalarTypeName + ">(" + value + ".AsString)",
            _ => throw new InvalidOperationException("Unsupported generated execution scalar conversion.")
        };
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

    private sealed record ModelResult(
        ModelDescriptor? Model,
        string TypeName,
        DiagnosticLocationDescriptor? DiagnosticLocation,
        DiagnosticKind DiagnosticKind,
        string? Error)
    {
        public static ModelResult Supported(ModelDescriptor model) => new(model, model.TypeName, null, DiagnosticKind.None, null);
        public static ModelResult InvalidModel(string typeName, DiagnosticLocationDescriptor diagnosticLocation, string error) =>
            new(null, typeName, diagnosticLocation, DiagnosticKind.InvalidModel, error);
        public static ModelResult InvalidProperty(string typeName, DiagnosticLocationDescriptor diagnosticLocation, string error) =>
            new(null, typeName, diagnosticLocation, DiagnosticKind.InvalidProperty, error);
        public static ModelResult MappingConflict(string typeName, DiagnosticLocationDescriptor diagnosticLocation, string error) =>
            new(null, typeName, diagnosticLocation, DiagnosticKind.MappingConflict, error);
    }

    private enum DiagnosticKind
    {
        None,
        InvalidModel,
        InvalidProperty,
        MappingConflict
    }

    private sealed record DiagnosticLocationDescriptor(
        string FilePath,
        TextSpan SourceSpan,
        LinePositionSpan LineSpan)
    {
        public Location Create() => Location.Create(FilePath, SourceSpan, LineSpan);
    }

    private sealed record ModelDescriptor(
        string TypeName,
        ImmutableArray<PropertyDescriptor> Properties,
        bool CanEmitExecutionMap);

    private sealed record PropertyDescriptor(
        string Name,
        string Identifier,
        string TypeName,
        string FieldName,
        PropertyKind Kind,
        ScalarConversionKind ScalarKind,
        string ScalarTypeName,
        bool IsNullableScalar,
        bool HasBsonId,
        bool AutoId,
        bool IsId);

    private enum ScalarConversionKind
    {
        None,
        Boolean,
        Byte,
        SByte,
        Char,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        String,
        ByteArray,
        DateTime,
        Guid,
        ObjectId,
        Enum
    }

    private enum PropertyKind
    {
        Scalar,
        StringList,
        StringArray,
        DynamicDictionary,
        DateTimeOffset,
        NullableDateTimeOffset,
        Unsupported
    }
}
