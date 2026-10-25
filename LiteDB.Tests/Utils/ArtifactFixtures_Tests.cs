using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests
{
    public class ArtifactFixtures_Tests : IDisposable
    {
        private const string Revision = "0123456789abcdef0123456789abcdef01234567";
        private const string Name = "Sample.zip";

        private static readonly byte[] _content = Encoding.UTF8.GetBytes("litedb artifact fixture");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "litedb-artifacts-test-" + Guid.NewGuid().ToString("n"));

        public ArtifactFixtures_Tests()
        {
            Directory.CreateDirectory(_root);
        }

        [Fact]
        public void Override_Directory_Returns_Verified_File()
        {
            var overrideDir = Path.Combine(_root, "override");
            var expected = this.WriteFixture(overrideDir);
            var manifest = CreateManifest(this.HashOf(expected));

            var path = ArtifactFixtures.Resolve(manifest, Name, overrideDir, Path.Combine(_root, "cache"), NoDownload);

            path.Should().Be(expected);
            File.ReadAllBytes(path).Should().Equal(_content);
            Directory.Exists(Path.Combine(_root, "cache")).Should().BeFalse();
        }

        [Fact]
        public void Hash_Mismatch_Throws_With_Expected_And_Actual()
        {
            var overrideDir = Path.Combine(_root, "override");
            var file = this.WriteFixture(overrideDir);
            var actual = this.HashOf(file);
            var wrong = new string('0', 64);
            var manifest = CreateManifest(wrong);

            Action act = () => ArtifactFixtures.Resolve(manifest, Name, overrideDir, Path.Combine(_root, "cache"), NoDownload);

            act.Should().Throw<InvalidDataException>()
                .Where(x => x.Message.Contains(wrong) && x.Message.Contains(actual) && x.Message.Contains(ArtifactFixtures.DirectoryVariable));
        }

        [Fact]
        public void Unknown_Name_Throws_Naming_Manifest()
        {
            var manifest = CreateManifest(new string('0', 64));

            Action act = () => ArtifactFixtures.Resolve(manifest, "Missing.zip", null, Path.Combine(_root, "cache"), NoDownload);

            act.Should().Throw<KeyNotFoundException>()
                .Where(x => x.Message.Contains("Missing.zip") && x.Message.Contains("test-manifest.json"));
        }

        [Fact]
        public void Download_Is_Verified_Then_Served_From_Cache()
        {
            var source = this.WriteFixture(Path.Combine(_root, "remote"));
            var manifest = CreateManifest(this.HashOf(source));
            var cacheRoot = Path.Combine(_root, "cache");
            var urls = new List<string>();

            Action<string, string> download = (url, target) =>
            {
                urls.Add(url);
                File.Copy(source, target);
            };

            var first = ArtifactFixtures.Resolve(manifest, Name, null, cacheRoot, download);
            var second = ArtifactFixtures.Resolve(manifest, Name, null, cacheRoot, download);

            first.Should().Be(Path.Combine(cacheRoot, Revision, "fixtures", Name));
            second.Should().Be(first);
            urls.Should().Equal(ArtifactFixtures.RawBaseUrl + Revision + "/fixtures/" + Name);
            Directory.GetFiles(Path.GetDirectoryName(first)).Should().ContainSingle();

            File.WriteAllText(first, "tampered");

            Action act = () => ArtifactFixtures.Resolve(manifest, Name, null, cacheRoot, download);

            act.Should().Throw<InvalidDataException>().Where(x => x.Message.Contains("cache file"));
        }

        [Fact]
        public void Embedded_Manifest_Pins_A_Revision()
        {
            using (var stream = typeof(ArtifactFixtures).Assembly.GetManifestResourceStream(ArtifactFixtures.ManifestResource))
            using (var reader = new StreamReader(stream))
            {
                var manifest = ArtifactFixtures.ParseManifest(reader.ReadToEnd(), "artifacts.json");

                manifest.Revision.Should().MatchRegex("^[0-9a-f]{40}$");
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private static ArtifactManifest CreateManifest(string sha256)
        {
            var files = new Dictionary<string, ArtifactEntry>
            {
                [Name] = new ArtifactEntry("fixtures/" + Name, sha256),
            };

            return new ArtifactManifest("test-manifest.json", Revision, files);
        }

        private static void NoDownload(string url, string target)
        {
            throw new InvalidOperationException("Network access is not allowed in this test: " + url);
        }

        private string WriteFixture(string directory)
        {
            var path = Path.Combine(directory, "fixtures", Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, _content);

            return path;
        }

        private string HashOf(string path)
        {
            return ArtifactFixtures.ComputeSha256(path);
        }
    }
}
