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
public sealed partial class BsonSourceGenerator : IIncrementalGenerator
{
    private const string SourceGeneratedAttributeName = "LiteDB.BsonSourceGeneratedAttribute";
    private const string BsonIdAttributeName = "LiteDB.BsonIdAttribute";
    private const string BsonFieldAttributeName = "LiteDB.BsonFieldAttribute";
    private const string BsonIgnoreAttributeName = "LiteDB.BsonIgnoreAttribute";
    private const string GeneratedMappingsHintName = "LiteDbGeneratedMappings.v2.g.cs";

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
                hintName: GeneratedMappingsHintName,
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

        if (type.IsSealed == false)
        {
            return ModelResult.InvalidModel(typeName, diagnosticLocation, "it must be sealed because generated collections do not support derived runtime types");
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
            CanEmitExecutionMap(properties)));
    }
}
