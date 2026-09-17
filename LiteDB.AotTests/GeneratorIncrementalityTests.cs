using System;
using System.IO;
using System.Linq;

using LiteDB.SourceGenerator;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static LiteDB.AotTests.GeneratorIncrementalityAssert;

namespace LiteDB.AotTests;

[TestClass]
public sealed class GeneratorIncrementalityTests
{
    [TestMethod]
    public void BsonSourceGenerator_EquivalentCompilation_CachesPipelineAndSourceOutput()
    {
        const string source = """
                using LiteDB;

                namespace IncrementalConsumer;

                [BsonSourceGenerated]
                public sealed class IncrementalRecord
                {
                    public int Id { get; set; }
                    public string Name { get; set; } = string.Empty;
                }
                """;

        var compilation = CSharpCompilation.Create(
            assemblyName: "IncrementalConsumer",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, path: "IncrementalRecord.cs")],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driverOptions = new GeneratorDriverOptions(
            disabledOutputs: IncrementalGeneratorOutputKind.None,
            trackIncrementalGeneratorSteps: true);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new BsonSourceGenerator().AsSourceGenerator()],
            driverOptions: driverOptions);

        driver = driver.RunGenerators(compilation);
        var firstResult = driver.GetRunResult().Results.Single();

        driver = driver.RunGenerators(compilation.Clone());
        var secondResult = driver.GetRunResult().Results.Single();

        Assert.AreEqual(
            firstResult.GeneratedSources.Single().SourceText.ToString(),
            secondResult.GeneratedSources.Single().SourceText.ToString());
        foreach (var stepName in ModelStepNames)
        {
            AssertEquivalentOutputs(firstResult.TrackedSteps, secondResult.TrackedSteps, stepName);
            AssertCacheable(secondResult.TrackedSteps, stepName);
        }

        AssertCacheable(secondResult.TrackedOutputSteps, "SourceOutput");
        AssertNoRoslynObjects(firstResult.TrackedSteps);
        AssertNoRoslynObjects(secondResult.TrackedSteps);
    }

    [TestMethod]
    public void BsonSourceGenerator_TriviaOnlyChange_ReusesSemanticModelAndSourceOutput()
    {
        const string originalSource = """
            using LiteDB;

            namespace IncrementalConsumer;

            [BsonSourceGenerated]
            public sealed class IncrementalRecord
            {
                public int Id { get; set; }
                public string Name { get; set; } = string.Empty;
            }
            """;
        const string modifiedSource = """
            // This shifts every source location without changing the generated mapping.

            using LiteDB;

            namespace IncrementalConsumer;

            [BsonSourceGenerated]
            public sealed class IncrementalRecord
            {
                public int Id { get; set; }

                public string Name { get; set; } = string.Empty; // trivia only
            }
            """;

        var originalTree = CSharpSyntaxTree.ParseText(originalSource, path: "IncrementalRecord.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "IncrementalConsumer",
            syntaxTrees: [originalTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driverOptions = new GeneratorDriverOptions(
            disabledOutputs: IncrementalGeneratorOutputKind.None,
            trackIncrementalGeneratorSteps: true);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new BsonSourceGenerator().AsSourceGenerator()],
            driverOptions: driverOptions);

        driver = driver.RunGenerators(compilation);
        var firstResult = driver.GetRunResult().Results.Single();
        var modifiedTree = CSharpSyntaxTree.ParseText(modifiedSource, path: "IncrementalRecord.cs");

        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(originalTree, modifiedTree));
        var secondResult = driver.GetRunResult().Results.Single();

        Assert.AreEqual(
            firstResult.GeneratedSources.Single().SourceText.ToString(),
            secondResult.GeneratedSources.Single().SourceText.ToString());
        foreach (var stepName in ModelStepNames)
        {
            AssertEquivalentOutputs(firstResult.TrackedSteps, secondResult.TrackedSteps, stepName);
            AssertCacheable(secondResult.TrackedSteps, stepName);
        }

        AssertCacheable(secondResult.TrackedOutputSteps, "SourceOutput");
        AssertNoRoslynObjects(secondResult.TrackedSteps);
    }

    [TestMethod]
    public void BsonSourceGenerator_ModelChange_InvalidatesPipelineAndChangesSourceOutput()
    {
        const string originalSource = """
                using LiteDB;

                [BsonSourceGenerated]
                public sealed class IncrementalRecord
                {
                    public int Id { get; set; }
                }
                """;
        const string modifiedSource = """
                using LiteDB;

                [BsonSourceGenerated]
                public sealed class IncrementalRecord
                {
                    public int Id { get; set; }
                    public long Score { get; set; }
                }
                """;

        var originalTree = CSharpSyntaxTree.ParseText(originalSource, path: "IncrementalRecord.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "IncrementalConsumer",
            syntaxTrees: [originalTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driverOptions = new GeneratorDriverOptions(
            disabledOutputs: IncrementalGeneratorOutputKind.None,
            trackIncrementalGeneratorSteps: true);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new BsonSourceGenerator().AsSourceGenerator()],
            driverOptions: driverOptions);

        driver = driver.RunGenerators(compilation);
        var originalResult = driver.GetRunResult().Results.Single();
        var modifiedTree = CSharpSyntaxTree.ParseText(modifiedSource, path: "IncrementalRecord.cs");

        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(originalTree, modifiedTree));
        var modifiedResult = driver.GetRunResult().Results.Single();

        var originalGeneratedSource = originalResult.GeneratedSources.Single().SourceText.ToString();
        var modifiedGeneratedSource = modifiedResult.GeneratedSources.Single().SourceText.ToString();
        Assert.AreNotEqual(originalGeneratedSource, modifiedGeneratedSource);
        StringAssert.Contains(modifiedGeneratedSource, "MemberName = \"Score\"");
        foreach (var stepName in ModelStepNames)
        {
            AssertRecomputed(modifiedResult.TrackedSteps, stepName);
        }

        AssertRecomputed(modifiedResult.TrackedOutputSteps, "SourceOutput");
    }

    [TestMethod]
    public void BsonSourceGenerator_OneModelChange_ReusesUnchangedModelAnalysis()
    {
        const string unchangedSource = """
            using LiteDB;

            [BsonSourceGenerated]
            public sealed class UnchangedRecord
            {
                public int Id { get; set; }
            }
            """;
        const string originalChangedSource = """
            using LiteDB;

            [BsonSourceGenerated]
            public sealed class ChangedRecord
            {
                public int Id { get; set; }
            }
            """;
        const string modifiedChangedSource = """
            using LiteDB;

            [BsonSourceGenerated]
            public sealed class ChangedRecord
            {
                public int Id { get; set; }
                public string Name { get; set; } = string.Empty;
            }
            """;

        var unchangedTree = CSharpSyntaxTree.ParseText(unchangedSource, path: "UnchangedRecord.cs");
        var changedTree = CSharpSyntaxTree.ParseText(originalChangedSource, path: "ChangedRecord.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "IncrementalConsumer",
            syntaxTrees: [unchangedTree, changedTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driverOptions = new GeneratorDriverOptions(
            disabledOutputs: IncrementalGeneratorOutputKind.None,
            trackIncrementalGeneratorSteps: true);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new BsonSourceGenerator().AsSourceGenerator()],
            driverOptions: driverOptions);

        driver = driver.RunGenerators(compilation);
        var modifiedTree = CSharpSyntaxTree.ParseText(modifiedChangedSource, path: "ChangedRecord.cs");
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(changedTree, modifiedTree));
        var result = driver.GetRunResult().Results.Single();

        AssertContainsStableAndModifiedOutputs(result.TrackedSteps, "BsonSourceGenerator.Models");
        AssertRecomputed(result.TrackedSteps, "BsonSourceGenerator.CollectedModels");
        AssertRecomputed(result.TrackedOutputSteps, "SourceOutput");
    }

    private static MetadataReference[] GetMetadataReferences()
    {
        var trustedPlatformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var runtimeReferences = trustedPlatformAssemblies
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var liteDbReference = MetadataReference.CreateFromFile(typeof(BsonSourceGeneratedAttribute).Assembly.Location);

        return runtimeReferences.Append(liteDbReference).ToArray();
    }
}
