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
        public void BsonSourceGenerator_AbstractModel_ReportsLdbsg001OnAnnotatedClassIdentifier()
        {
            const string source = """
                using LiteDB;

                namespace ExternalConsumer;

                [BsonSourceGenerated]
                public abstract class InvalidDiagnosticRecord
                {
                    public int Id { get; set; }
                }
                """;

            AssertDiagnostic(
                source,
                "InvalidDiagnosticRecord.cs",
                "LDBSG001",
                "InvalidDiagnosticRecord",
                "non-abstract");
        }

        [TestMethod]
        public void BsonSourceGenerator_RecordClassWithInitOnlyProperty_ReportsLdbsg002OnRecordIdentifier()
        {
            const string source = """
                using LiteDB;

                namespace ExternalConsumer;

                [BsonSourceGenerated]
                public record InvalidRecordDiagnostic
                {
                    public int Id { get; init; }
                }
                """;

            AssertDiagnostic(
                source,
                "InvalidRecordDiagnostic.cs",
                "LDBSG002",
                "InvalidRecordDiagnostic",
                "public non-init getter and setter");
        }

        [TestMethod]
        public void BsonSourceGenerator_HiddenMappedProperty_ReportsLdbsg003OnAnnotatedClassIdentifier()
        {
            const string source = """
                using LiteDB;

                namespace ExternalConsumer;

                public class HiddenBaseRecord
                {
                    public int Id { get; set; }
                }

                [BsonSourceGenerated]
                public sealed class HiddenDerivedRecord : HiddenBaseRecord
                {
                    public new int Id { get; set; }
                }
                """;

            AssertDiagnostic(
                source,
                "HiddenDerivedRecord.cs",
                "LDBSG003",
                "HiddenDerivedRecord",
                "multiple mapped properties are named");
        }

        [TestMethod]
        public void BsonSourceGenerator_UnsupportedDirectProperty_ReportsLdbsg002OnAnnotatedClassIdentifier()
        {
            const string source = """
                using LiteDB;

                namespace ExternalConsumer;

                [BsonSourceGenerated]
                public sealed class InvalidDirectPropertyRecord
                {
                    public int Id { get; set; }
                    public int[] UnsupportedValues { get; set; } = System.Array.Empty<int>();
                }
                """;

            AssertDiagnostic(
                source,
                "InvalidDirectPropertyRecord.cs",
                "LDBSG002",
                "InvalidDirectPropertyRecord",
                "unsupported type");
        }

        [TestMethod]
        public void BsonSourceGenerator_UnsupportedInheritedProperty_ReportsLdbsg002OnAnnotatedClassIdentifier()
        {
            const string source = """
                using LiteDB;

                namespace ExternalConsumer;

                public class UnsupportedBaseRecord
                {
                    public int[] UnsupportedValues { get; set; } = System.Array.Empty<int>();
                }

                [BsonSourceGenerated]
                public sealed class InvalidInheritedPropertyRecord : UnsupportedBaseRecord
                {
                    public int Id { get; set; }
                }
                """;

            AssertDiagnostic(
                source,
                "InvalidInheritedPropertyRecord.cs",
                "LDBSG002",
                "InvalidInheritedPropertyRecord",
                "unsupported type");
        }

        [TestMethod]
        public void BsonSourceGenerator_PersistedGetterOnlyProperty_ReportsLdbsg002OnAnnotatedClassIdentifier()
        {
            const string source = """
                using LiteDB;

                namespace ExternalConsumer;

                [BsonSourceGenerated]
                public sealed class GetterOnlyPropertyRecord
                {
                    public int Id { get; set; }

                    [BsonField("fingerprint")]
                    public string Fingerprint => Id.ToString();
                }
                """;

            AssertDiagnostic(
                source,
                "GetterOnlyPropertyRecord.cs",
                "LDBSG002",
                "GetterOnlyPropertyRecord",
                "public non-init getter and setter");
        }

        [TestMethod]
        public void BsonSourceGenerator_DuplicateBsonFieldNames_ReportsLdbsg003OnAnnotatedClassIdentifier()
        {
            const string source = """
                using LiteDB;

                namespace ExternalConsumer;

                [BsonSourceGenerated]
                public sealed class DuplicateFieldRecord
                {
                    public int Id { get; set; }

                    [BsonField("duplicate")]
                    public string First { get; set; } = string.Empty;

                    [BsonField("duplicate")]
                    public string Second { get; set; } = string.Empty;
                }
                """;

            AssertDiagnostic(
                source,
                "DuplicateFieldRecord.cs",
                "LDBSG003",
                "DuplicateFieldRecord",
                "BSON field name");
        }

        private static void AssertDiagnostic(
            string source,
            string sourcePath,
            string expectedDiagnosticId,
            string expectedIdentifier,
            string expectedMessageFragment)
        {
            var sourceTree = CSharpSyntaxTree.ParseText(source, path: sourcePath);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ExternalConsumer",
                syntaxTrees: [sourceTree],
                references: GetMetadataReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new BsonSourceGenerator().AsSourceGenerator());

            driver = driver.RunGenerators(compilation);

            var diagnostics = driver.GetRunResult().Results.Single().Diagnostics;
            var diagnostic = diagnostics.Single(item => item.Id == expectedDiagnosticId);
            var expectedStart = source.IndexOf(expectedIdentifier, StringComparison.Ordinal);

            Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Location.IsInMetadata);
            Assert.AreEqual(sourcePath, diagnostic.Location.GetLineSpan().Path);
            Assert.AreEqual(expectedStart, diagnostic.Location.SourceSpan.Start);
            Assert.AreEqual(expectedIdentifier, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
            StringAssert.Contains(diagnostic.GetMessage(), expectedMessageFragment);
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
