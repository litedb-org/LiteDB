using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class HnswFixtureRuntime_Tests
    {
        [Theory]
        [InlineData("HnswAsymmetric_6b2ecd3af.zip")]
        [InlineData("HnswDeferred_6b2ecd3af.zip")]
        [InlineData("HnswMutation20000_6b2ecd3af.zip")]
        [InlineData("disconnected")]
        public void Portable_copy_preserves_every_graph_and_document_byte_and_original(string name)
        {
            using var source = name == "disconnected"
                ? typeof(HnswFixtureRuntime_Tests).Assembly.GetManifestResourceStream(
                    "LiteDB.Tests.Resources.HnswDisconnected_dev_b3c440623.zip")
                : File.OpenRead(ArtifactFixtures.Path(name));
            using var zip = new ZipArchive(source);
            using var entry = zip.GetEntry("vectors.db").Open();
            using var output = new MemoryStream();
            entry.CopyTo(output);
            var original = output.ToArray();
            var retained = (byte[])original.Clone();
            var prepared = HnswFixtureRuntime.Prepare(original);
            original.Should().Equal(retained);
            prepared.Should().NotBeSameAs(original);
            prepared.Skip(8192).Should().Equal(original.Skip(8192));
            Enumerable.Range(0, prepared.Length).Where(i => prepared[i] != original[i])
                .Should().OnlyContain(i => (i >= 14 && i < 18) || (i >= 92 && i < 96));
            BitConverter.ToUInt32(prepared, 92).Should().Be(0);
            original[8192] ^= 1;
            Action damaged = () => HnswFixtureRuntime.Prepare(original);
            damaged.Should().Throw<InvalidDataException>();
        }
    }
}
