using System;
using System.Linq;
using System.Text;

using LiteDB.SourceGenerator.Analysis;
using LiteDB.SourceGenerator.Emission;
using LiteDB.SourceGenerator.Models;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LiteDB.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class BsonSourceGenerator : IIncrementalGenerator
{
    private const string SourceGeneratedAttributeName = "LiteDB.BsonSourceGeneratedAttribute";
    private const string GeneratedMappingsHintName = "LiteDbGeneratedMappings.v2.g.cs";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: SourceGeneratedAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (attributeContext, cancellationToken) => BsonModelAnalyzer.Describe(
                    (INamedTypeSymbol)attributeContext.TargetSymbol,
                    DiagnosticLocationDescriptor.From(attributeContext.TargetNode),
                    cancellationToken))
            .WithTrackingName(GeneratorTrackingNames.Models);

        var diagnostics = results
            .Where(static result => !result.IsSupported)
            .WithTrackingName(GeneratorTrackingNames.Diagnostics);

        context.RegisterSourceOutput(diagnostics, static (productionContext, result) =>
        {
            productionContext.ReportDiagnostic(Diagnostic.Create(
                GeneratorDiagnostics.GetDescriptor(result.DiagnosticKind),
                result.DiagnosticLocation!.ToLocation(),
                result.TypeName,
                result.Error));
        });

        var models = results
            .Where(static result => result.IsSupported)
            .Select(static (result, _) => result.Model!)
            .WithTrackingName(GeneratorTrackingNames.ValidModels);

        var collectedModels = models.Collect()
            .WithTrackingName(GeneratorTrackingNames.CollectedModels);

        context.RegisterSourceOutput(collectedModels, static (productionContext, descriptors) =>
        {
            if (descriptors.IsDefaultOrEmpty)
            {
                return;
            }

            var models = descriptors.ToArray();
            Array.Sort(models, static (left, right) => StringComparer.Ordinal.Compare(left.TypeName, right.TypeName));
            var source = GeneratedMappingsEmitter.Generate(models, productionContext.CancellationToken);

            productionContext.AddSource(
                GeneratedMappingsHintName,
                SourceText.From(source, Encoding.UTF8));
        });
    }
}
