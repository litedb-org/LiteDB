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
    public sealed class GeneratedSourceSnapshotTests
    {
        [TestMethod]
        public void BsonSourceGenerator_BasicInheritedModel_EmitsRegistrarAndFactorySemanticSnapshot()
        {
            const string source = """
                using LiteDB;

                namespace SnapshotConsumer;

                public abstract class SnapshotBase
                {
                    [BsonId]
                    public int Id { get; set; }

                    [BsonField("name")]
                    public string Name { get; set; } = string.Empty;
                }

                [BsonSourceGenerated]
                public sealed class SnapshotRecord : SnapshotBase
                {
                    public int? RetryCount { get; set; }

                    public string Fingerprint => Name + RetryCount;
                }
                """;

            var generatedSource = GenerateSource(source);

            AssertContainsInOrder(
                generatedSource,
                "public static void Register(global::LiteDB.BsonMapper mapper)",
                "var map0 = Create0();",
                "mapper.RegisterGeneratedEntityMapper(map0);",
                "private static global::LiteDB.EntityMapper Create0()",
                "FieldName = \"_id\",",
                "MemberName = \"Id\",",
                "FieldName = \"name\",",
                "MemberName = \"Name\",",
                "MemberName = \"RetryCount\",",
                "DataType = typeof(global::System.Int32?),");
            Assert.IsFalse(generatedSource.Contains("MemberName = \"Fingerprint\"", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BsonSourceGenerator_MutableRecordClass_EmitsDeclaredPropertyMappingsOnly()
        {
            const string source = """
                using LiteDB;

                namespace SnapshotConsumer;

                [BsonSourceGenerated]
                public sealed record SnapshotMutableRecord
                {
                    public int Id { get; set; }
                    public string Name { get; set; } = string.Empty;
                }
                """;

            var generatedSource = GenerateSource(source);

            AssertContainsInOrder(
                generatedSource,
                "CreateInstance = _ => new global::SnapshotConsumer.SnapshotMutableRecord()",
                "MemberName = \"Id\",",
                "MemberName = \"Name\",");
            Assert.IsFalse(generatedSource.Contains("MemberName = \"EqualityContract\"", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BsonSourceGenerator_OverrideChain_EmitsOnlyTheMostDerivedProperty()
        {
            const string source = """
                using LiteDB;

                namespace SnapshotConsumer;

                public class OverrideBase
                {
                    [BsonId(false)]
                    public virtual int Key { get; set; }
                }

                public class OverrideMiddle : OverrideBase
                {
                    public override int Key { get; set; }
                }

                [BsonSourceGenerated]
                public sealed class OverrideSnapshotRecord : OverrideMiddle
                {
                    public override int Key { get; set; }
                    public string Name { get; set; } = string.Empty;
                }
                """;

            var generatedSource = GenerateSource(source);

            AssertContainsInOrder(
                generatedSource,
                "FieldName = \"_id\",",
                "MemberName = \"Key\",",
                "Setter = (entity, value) => ((global::SnapshotConsumer.OverrideSnapshotRecord)entity).Key = (global::System.Int32)value",
                "MemberName = \"Name\",");
            Assert.AreEqual(1, generatedSource.Split("MemberName = \"Key\"", StringSplitOptions.None).Length - 1);
        }

        [TestMethod]
        public void BsonSourceGenerator_ConditionalHelpers_EmitsDateTimeOffsetStringArrayAndDynamicDictionarySemanticSnapshot()
        {
            const string source = """
                using System;
                using System.Collections.Generic;
                using LiteDB;

                namespace SnapshotConsumer;

                [BsonSourceGenerated]
                public sealed class HelperSnapshotRecord
                {
                    public int Id { get; set; }
                    public DateTimeOffset OccurredAt { get; set; }
                    public DateTimeOffset? DeliveredAt { get; set; }
                    public string[] StreamNames { get; set; } = [];
                    public Dictionary<string, object?> Fields { get; set; } = [];
                }
                """;

            var generatedSource = GenerateSource(source);

            AssertContainsInOrder(
                generatedSource,
                "private static global::LiteDB.BsonValue SerializeStringArray(string[]? values)",
                "private static string[]? DeserializeStringArray(global::LiteDB.BsonValue value)",
                "private static global::LiteDB.BsonValue SerializeDynamicDictionary(global::System.Collections.Generic.Dictionary<string, object?>? values)",
                "private static global::System.Collections.Generic.Dictionary<string, object?>? DeserializeDynamicDictionary(global::LiteDB.BsonValue value)",
                "private static global::LiteDB.BsonValue SerializeDateTimeOffset(object? value)",
                "private static object DeserializeDateTimeOffset(global::LiteDB.BsonValue value)",
                "if (value.IsDateTime)",
                "return new global::System.DateTimeOffset(value.AsDateTime.ToUniversalTime());",
                "Serialize = (value, _) => SerializeDateTimeOffset(value),",
                "Serialize = (value, _) => SerializeStringArray((string[])value),",
                "Serialize = (value, _) => SerializeDynamicDictionary((global::System.Collections.Generic.Dictionary<string, object?>)value),",
                "Setter = (entity, value) => ((global::SnapshotConsumer.HelperSnapshotRecord)entity).Fields = (global::System.Collections.Generic.Dictionary<string, object?>)value");
        }


        [TestMethod]
        public void BsonSourceGenerator_C2Scalar_EmitsAutomaticExecutionMapSemanticSnapshot()
        {
            const string source = """
                using LiteDB;

                namespace SnapshotConsumer;

                [BsonSourceGenerated]
                public sealed class C1ScalarRecord
                {
                    public int Id { get; set; }
                    public string? Name { get; set; }
                    public int Score { get; set; }
                }
                """;

            var generatedSource = GenerateSource(source);

            AssertContainsInOrder(
                generatedSource,
                "mapper.RegisterGeneratedExecutionMap(CreateExecutionMap0());",
                "private static global::LiteDB.GeneratedEntityMap<global::SnapshotConsumer.C1ScalarRecord> CreateExecutionMap0()",
                "document[\"_id\"] = new global::LiteDB.BsonValue(entity.Id);",
                "if (entity.Name is null)",
                "var text = options.TrimWhitespace ? entity.Name.Trim() : entity.Name;",
                "document[\"Name\"] = options.EmptyStringToNull && text.Length == 0 ? global::LiteDB.BsonValue.Null : new global::LiteDB.BsonValue(text);",
                "entity.Score = value2.AsInt32;");
        }

        [TestMethod]
        public void BsonSourceGenerator_StringCollections_EmitDirectExecutionLoopsAndKnownMaterializers()
        {
            const string source = """
                using System.Collections.Generic;
                using LiteDB;

                namespace SnapshotConsumer;

                [BsonSourceGenerated]
                public sealed class StringCollectionsRecord
                {
                    public int Id { get; set; }
                    public List<string>? Names { get; set; }
                    public string[]? Aliases { get; set; }
                }
                """;

            var generatedSource = GenerateSource(source);

            AssertContainsInOrder(
                generatedSource,
                "mapper.RegisterGeneratedExecutionMap(CreateExecutionMap0());",
                "foreach (var item in entity.Names)",
                "var text = options.TrimWhitespace ? item.Trim() : item;",
                "document[\"Names\"] = array;",
                "foreach (var item in entity.Aliases)",
                "document[\"Aliases\"] = array;",
                "new global::System.Collections.Generic.List<string>(value1.AsArray.Count)",
                "result1.Add(item.IsNull ? null! : item.AsString);",
                "var result2 = new string[array2.Count];");
        }

        [TestMethod]
        public void BsonSourceGenerator_C2ScalarMatrix_EmitsDirectScalarCompatibilityConversions()
        {
            const string source = """
                using LiteDB;

                namespace SnapshotConsumer;

                public enum ScalarState
                {
                    Ready = 1
                }

                [BsonSourceGenerated]
                public sealed class ScalarCompatibilityRecord
                {
                    public int Id { get; set; }
                    public uint Unsigned { get; set; }
                    public ScalarState State { get; set; }
                    public ScalarState? NullableState { get; set; }
                    public ObjectId? NullableObjectId { get; set; }
                    public byte[]? Payload { get; set; }
                }
                """;

            var generatedSource = GenerateSource(source);

            AssertContainsInOrder(
                generatedSource,
                "document[\"Unsigned\"] = new global::LiteDB.BsonValue((long)entity.Unsigned);",
                "document[\"State\"] = options.EnumAsInteger ? new global::LiteDB.BsonValue((int)entity.State) : new global::LiteDB.BsonValue(entity.State.ToString());",
                "if (entity.NullableState is null)",
                "document[\"NullableState\"] = options.EnumAsInteger ? new global::LiteDB.BsonValue((int)entity.NullableState.Value) : new global::LiteDB.BsonValue(entity.NullableState.Value.ToString());",
                "if (entity.NullableObjectId is null)",
                "entity.NullableState = value3.IsInt32 ? (global::SnapshotConsumer.ScalarState)value3.AsInt32 : global::System.Enum.Parse<global::SnapshotConsumer.ScalarState>(value3.AsString);",
                "entity.NullableObjectId = value4.AsObjectId;");
        }


        private static string GenerateSource(string source)
        {
            var sourceTree = CSharpSyntaxTree.ParseText(source, path: "SnapshotConsumer.cs");
            var compilation = CSharpCompilation.Create(
                assemblyName: "SnapshotConsumer",
                syntaxTrees: [sourceTree],
                references: GetMetadataReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new BsonSourceGenerator().AsSourceGenerator());

            driver = driver.RunGenerators(compilation);

            var result = driver.GetRunResult().Results.Single();
            Assert.AreEqual(0, result.Diagnostics.Length);
            Assert.AreEqual(1, result.GeneratedSources.Length);
            return result.GeneratedSources[0].SourceText.ToString();
        }

        private static void AssertContainsInOrder(string generatedSource, params string[] fragments)
        {
            var previousIndex = -1;
            foreach (var fragment in fragments)
            {
                var index = generatedSource.IndexOf(fragment, previousIndex + 1, StringComparison.Ordinal);
                Assert.IsTrue(index >= 0, $"Generated source does not contain the expected snapshot fragment after the previous snapshot element: {fragment}");
                previousIndex = index;
            }
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
