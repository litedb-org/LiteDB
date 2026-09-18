using System;
using System.Linq;
using System.Text;

using LiteDB.SourceGenerator.Analysis;
using LiteDB.SourceGenerator.Emission;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LiteDB.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class BsonSourceGenerator : IIncrementalGenerator
{
    private const string GeneratedMappingsHintName = "LiteDbGeneratedMappings.g.cs";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: BsonSourceGenerationMetadataNames.Attribute,
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (attributeContext, cancellationToken) => BsonModelAnalyzer.TryDescribe(
                    (INamedTypeSymbol)attributeContext.TargetSymbol,
                    cancellationToken))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(GeneratorTrackingNames.Models);

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
