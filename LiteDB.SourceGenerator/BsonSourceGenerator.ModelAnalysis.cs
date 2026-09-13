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

public sealed partial class BsonSourceGenerator
{
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
            "System.DateTimeOffset" => ScalarConversionKind.DateTimeOffset,
            "System.Guid" => ScalarConversionKind.Guid,
            "LiteDB.ObjectId" => ScalarConversionKind.ObjectId,
            _ => ScalarConversionKind.None
        };
    }

    private static bool CanEmitExecutionMap(IReadOnlyList<PropertyDescriptor> properties)
    {
        // Model analysis has already rejected every shape for which direct code cannot be
        // emitted. Keep this predicate explicit so newly admitted property kinds fail closed.
        return properties.All(property =>
            (property.Kind is PropertyKind.Scalar or PropertyKind.DateTimeOffset or PropertyKind.NullableDateTimeOffset &&
                property.ScalarKind != ScalarConversionKind.None) ||
            property.Kind is PropertyKind.StringList or PropertyKind.StringArray or PropertyKind.DynamicDictionary);
    }
}
