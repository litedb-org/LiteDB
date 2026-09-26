using System.Collections.Immutable;
using System.Linq;
using System.Threading;

using LiteDB.SourceGenerator.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LiteDB.SourceGenerator;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BsonSourceGenerationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        GeneratorDiagnostics.SupportedDiagnostics;

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            var markerAttribute = startContext.Compilation.GetTypeByMetadataName(BsonSourceGenerationMetadataNames.Attribute);
            if (markerAttribute is not null)
            {
                startContext.RegisterSymbolAction(
                    symbolContext => AnalyzeNamedType(symbolContext, markerAttribute),
                    SymbolKind.NamedType);
            }
        });
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol markerAttributeType)
    {
        if (context.Symbol is not INamedTypeSymbol type)
        {
            return;
        }

        var markerAttribute = type.GetAttributes().FirstOrDefault(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, markerAttributeType));
        if (markerAttribute is null)
        {
            return;
        }

        var result = BsonModelAnalyzer.Analyze(type, context.CancellationToken);
        if (result.IsSupported)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            GeneratorDiagnostics.GetDescriptor(result.DiagnosticKind),
            GetIdentifierLocation(type, markerAttribute, context.CancellationToken),
            SymbolNameFormatter.GetTypeName(type),
            result.Error));
    }

    private static Location? GetIdentifierLocation(
        INamedTypeSymbol type,
        AttributeData markerAttribute,
        CancellationToken cancellationToken)
    {
        var attributeSyntax = markerAttribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken);
        var declaration = attributeSyntax?.FirstAncestorOrSelf<TypeDeclarationSyntax>();

        return declaration?.Identifier.GetLocation() ?? type.Locations.FirstOrDefault();
    }
}
