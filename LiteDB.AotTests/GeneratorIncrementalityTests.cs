using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.SourceGenerator;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
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
            AssertCacheable(secondResult.TrackedSteps, "BsonSourceGenerator.Models");
            AssertCacheable(secondResult.TrackedSteps, "BsonSourceGenerator.CollectedModels");
            AssertCacheable(secondResult.TrackedOutputSteps, "SourceOutput");
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
            AssertRecomputed(modifiedResult.TrackedSteps, "BsonSourceGenerator.Models");
            AssertRecomputed(modifiedResult.TrackedSteps, "BsonSourceGenerator.CollectedModels");
            AssertRecomputed(modifiedResult.TrackedOutputSteps, "SourceOutput");
        }

        private static void AssertCacheable(
            IReadOnlyDictionary<string, System.Collections.Immutable.ImmutableArray<IncrementalGeneratorRunStep>> steps,
            string name)
        {
            Assert.IsTrue(steps.TryGetValue(name, out var namedSteps), $"Tracked generator step '{name}' was not recorded.");

            var reasons = namedSteps
                .SelectMany(step => step.Outputs)
                .Select(output => output.Reason)
                .ToArray();

            Assert.IsTrue(reasons.Length > 0, $"Tracked generator step '{name}' produced no outputs.");
            Assert.IsTrue(
                reasons.All(reason => reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
                $"Tracked generator step '{name}' was recomputed: {string.Join(", ", reasons)}.");
        }

        private static void AssertRecomputed(
            IReadOnlyDictionary<string, System.Collections.Immutable.ImmutableArray<IncrementalGeneratorRunStep>> steps,
            string name)
        {
            Assert.IsTrue(steps.TryGetValue(name, out var namedSteps), $"Tracked generator step '{name}' was not recorded.");
            Assert.IsTrue(
                namedSteps.SelectMany(step => step.Outputs).Any(output => output.Reason == IncrementalStepRunReason.Modified),
                $"Tracked generator step '{name}' did not observe the model change.");
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
}
