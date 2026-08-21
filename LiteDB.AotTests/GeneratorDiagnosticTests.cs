using System;
using System.IO;
using System.Linq;
using LiteDB.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    [TestClass]
    public sealed class GeneratorDiagnosticTests
    {
        [TestMethod]
        public void BsonSourceGenerator_UnsupportedModel_ReportsLdbsg001OnAnnotatedClassIdentifier()
        {
            const string source = """
                using LiteDB;

                namespace ExternalConsumer;

                [BsonSourceGenerated]
                public sealed class InvalidDiagnosticRecord
                {
                    public int Id { get; set; }
                    public int[] UnsupportedValues { get; set; } = System.Array.Empty<int>();
                }
                """;
            const string sourcePath = "InvalidDiagnosticRecord.cs";
            var sourceTree = CSharpSyntaxTree.ParseText(source, path: sourcePath);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ExternalConsumer",
                syntaxTrees: [sourceTree],
                references: GetMetadataReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new BsonSourceGenerator().AsSourceGenerator());

            driver = driver.RunGenerators(compilation);

            var diagnostics = driver.GetRunResult().Results.Single().Diagnostics;
            var diagnostic = diagnostics.Single(item => item.Id == "LDBSG001");
            var expectedStart = source.IndexOf("InvalidDiagnosticRecord", StringComparison.Ordinal);

            Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Location.IsInMetadata);
            Assert.AreEqual(sourcePath, diagnostic.Location.GetLineSpan().Path);
            Assert.AreEqual(expectedStart, diagnostic.Location.SourceSpan.Start);
            Assert.AreEqual("InvalidDiagnosticRecord", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
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
