using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using LiteDB.SourceGenerator;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests;

[TestClass]
public sealed class GeneratorAssemblyCompositionTests
{
    [TestMethod]
    public void TwoModelLibrariesAndAnApplication_CanEachRegisterMappingsWithoutTypeConflicts()
    {
        var first = Compile("FirstModels", ModelSource("FirstModels"));
        var second = Compile("SecondModels", ModelSource("SecondModels"), first);
        Compile("Application", ModelSource("Application") + """

            public static class Startup
            {
                public static void Register(LiteDB.BsonMapper mapper)
                {
                    FirstModels.Registration.Register(mapper);
                    SecondModels.Registration.Register(mapper);
                    Application.Registration.Register(mapper);
                }
            }
            """, first, second);
    }

    private static string ModelSource(string name) => $$"""
        namespace {{name}}
        {
            [LiteDB.BsonSourceGenerated]
            public sealed class Record
            {
                public int Id { get; set; }
            }

            public static class Registration
            {
                public static void Register(LiteDB.BsonMapper mapper)
                {
                    LiteDB.Generated.LiteDbGeneratedMappings.Register(mapper);
                }
            }
        }
        """;

    private static MetadataReference Compile(string assemblyName, string source, params MetadataReference[] libraries)
    {
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = trustedAssemblies.Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path) != "LiteDB.AotTests.dll")
            .Select(path => MetadataReference.CreateFromFile(path))
            .Concat(libraries)
            .Append(MetadataReference.CreateFromFile(typeof(BsonMapper).Assembly.Location));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions(ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("CS0436", ReportDiagnostic.Error));
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source)], references, options);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new BsonSourceGenerator().AsSourceGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        Assert.IsFalse(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", diagnostics));
        using var assembly = new MemoryStream();
        var result = output.Emit(assembly);
        Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(assembly.ToArray());
    }
}
