using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using LiteDB.SourceGenerator.Models;
using LiteDB.SourceGenerator.Utilities;

using Microsoft.CodeAnalysis;

namespace LiteDB.SourceGenerator.Analysis;

internal static class BsonModelAnalyzer
{
    private const string BsonIdAttributeName = "LiteDB.BsonIdAttribute";
    private const string BsonFieldAttributeName = "LiteDB.BsonFieldAttribute";
    private const string BsonIgnoreAttributeName = "LiteDB.BsonIgnoreAttribute";

    public static ModelDescriptor? TryDescribe(INamedTypeSymbol type, CancellationToken cancellationToken) =>
        Analyze(type, cancellationToken).Model;

    public static ModelAnalysisResult Analyze(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var typeName = SymbolNameFormatter.GetTypeName(type);

        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType || type.ContainingType is not null)
        {
            return ModelAnalysisResult.InvalidModel("it must be a non-abstract, non-generic, top-level class");
        }

        if (!type.IsSealed)
        {
            return ModelAnalysisResult.InvalidModel("it must be sealed because generated collections do not support derived runtime types");
        }

        if (type.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal)
        {
            return ModelAnalysisResult.InvalidModel("it must be public or internal");
        }

        if (!type.InstanceConstructors.Any(static constructor =>
                constructor.Parameters.Length == 0 &&
                constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal))
        {
            return ModelAnalysisResult.InvalidModel("an accessible parameterless constructor is required");
        }

        var hierarchy = new List<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (current.TypeKind != TypeKind.Class || current.IsGenericType || current.ContainingType is not null)
            {
                return ModelAnalysisResult.InvalidModel("all base classes must be non-generic, top-level classes");
            }

            if (current.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal)
            {
                return ModelAnalysisResult.InvalidModel("all base classes must be public or internal");
            }

            hierarchy.Add(current);
        }

        hierarchy.Reverse();

        var properties = new List<PropertyDescriptor>();
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in GetSelectedProperties(hierarchy, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasAttribute(property, BsonIgnoreAttributeName))
            {
                continue;
            }

            if (HasAttribute(property, "LiteDB.BsonRefAttribute"))
            {
                return ModelAnalysisResult.InvalidProperty($"property '{property.Name}' uses BsonRef, which generated mappings do not support");
            }

            if (property.IsStatic || property.IsIndexer)
            {
                return ModelAnalysisResult.InvalidProperty($"property '{property.Name}' must be a non-static, non-indexed property");
            }

            if (IsComputedProperty(property))
            {
                continue;
            }

            if (property.DeclaredAccessibility is not Accessibility.Public ||
                property.GetMethod is null || property.GetMethod.DeclaredAccessibility is not Accessibility.Public ||
                property.SetMethod is null || property.SetMethod.DeclaredAccessibility is not Accessibility.Public ||
                property.SetMethod.IsInitOnly)
            {
                return ModelAnalysisResult.InvalidProperty($"property '{property.Name}' must have public non-init getter and setter accessors");
            }

            if (!memberNames.Add(property.Name))
            {
                return ModelAnalysisResult.MappingConflict($"multiple mapped properties are named '{property.Name}' across the inheritance hierarchy");
            }

            var kind = PropertyTypeAnalyzer.GetPropertyKind(property.Type);
            if (kind == PropertyKind.Unsupported)
            {
                return ModelAnalysisResult.InvalidProperty($"property '{property.Name}' has an unsupported type '{property.Type.ToDisplayString()}'");
            }

            var scalarKind = PropertyTypeAnalyzer.GetScalarConversionKind(property.Type, out var isNullableScalar);
            var idAttribute = GetAttribute(property, BsonIdAttributeName);
            properties.Add(new PropertyDescriptor(
                Name: property.Name,
                Identifier: SymbolNameFormatter.EscapeIdentifier(property.Name),
                TypeName: SymbolNameFormatter.GetTypeName(property.Type),
                FieldName: GetFieldName(property),
                Kind: kind,
                ScalarKind: scalarKind,
                EnumUnderlyingKind: PropertyTypeAnalyzer.GetEnumUnderlyingConversionKind(property.Type),
                ScalarTypeName: GetScalarTypeName(property.Type),
                IsNullableScalar: isNullableScalar,
                HasBsonId: idAttribute is not null,
                IsDeclaringTypeId: string.Equals(property.Name, property.ContainingType.Name + "Id", StringComparison.OrdinalIgnoreCase),
                AutoId: GetAutoId(idAttribute),
                IsId: false));
        }

        if (properties.Count == 0)
        {
            return ModelAnalysisResult.InvalidModel("at least one supported property is required");
        }

        var explicitIds = properties.Where(static property => property.HasBsonId).ToArray();
        if (explicitIds.Length > 1)
        {
            return ModelAnalysisResult.MappingConflict("multiple properties are marked with BsonId");
        }

        PropertyDescriptor? id = null;
        if (explicitIds.Length == 1)
        {
            id = explicitIds[0];
        }
        else
        {
            // Match BsonMapper.GetIdMember: Id wins over <DeclaringTypeName>Id, including inherited members.
            var conventionalIds = properties.Where(static property =>
                string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (conventionalIds.Length == 0)
            {
                conventionalIds = properties.Where(static property => property.IsDeclaringTypeId).ToArray();
            }

            if (conventionalIds.Length > 1)
            {
                return ModelAnalysisResult.MappingConflict("multiple properties match generated ID conventions across the inheritance hierarchy");
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

        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (!fieldNames.Add(property.FieldName))
            {
                return ModelAnalysisResult.MappingConflict($"multiple mapped properties use BSON field name '{property.FieldName}' across the inheritance hierarchy");
            }
        }

        return ModelAnalysisResult.Supported(new ModelDescriptor(
            typeName,
            new EquatableArray<PropertyDescriptor>(properties)));
    }

    private static IReadOnlyList<IPropertySymbol> GetSelectedProperties(
        IReadOnlyList<INamedTypeSymbol> hierarchy,
        CancellationToken cancellationToken)
    {
        var overriddenAncestors = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var level in hierarchy)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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

    private static bool IsComputedProperty(IPropertySymbol property)
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
            !string.Equals(property.Name, property.ContainingType.Name + "Id", StringComparison.OrdinalIgnoreCase);
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

        return SymbolNameFormatter.GetTypeName(type);
    }
}
