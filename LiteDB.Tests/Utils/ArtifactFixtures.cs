using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using IOPath = System.IO.Path;

namespace LiteDB.Tests
{
    /// <summary>
    /// Resolves binary test fixtures stored in litedb-org/LiteDB-Artifacts and pinned by
    /// Resources/artifacts.json. Order: $LITEDB_ARTIFACTS_DIR, per-user cache, download.
    /// Every returned file has been verified against the manifest SHA-256.
    /// </summary>
    public static class ArtifactFixtures
    {
        public const string DirectoryVariable = "LITEDB_ARTIFACTS_DIR";
        public const string ManifestResource = "LiteDB.Tests.Resources.artifacts.json";
        public const string RawBaseUrl = "https://raw.githubusercontent.com/litedb-org/LiteDB-Artifacts/";

        private static readonly Lazy<ArtifactManifest> _manifest = new Lazy<ArtifactManifest>(LoadEmbeddedManifest);

        /// <summary>
        /// Return a local, hash-verified path for the named artifact.
        /// </summary>
        public static string Path(string name)
        {
            return Resolve(
                _manifest.Value,
                name,
                Environment.GetEnvironmentVariable(DirectoryVariable),
                IOPath.Combine(IOPath.GetTempPath(), "litedb-artifacts"),
                Download);
        }

        internal static string Resolve(
            ArtifactManifest manifest,
            string name,
            string overrideDirectory,
            string cacheRoot,
            Action<string, string> download)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            if (!manifest.Files.TryGetValue(name, out var entry))
            {
                throw new KeyNotFoundException(
                    $"Artifact '{name}' is not listed in manifest {manifest.Source}. " +
                    "Upload it to LiteDB-Artifacts and add it to LiteDB.Tests/Resources/artifacts.json.");
            }

            var relative = entry.Path.Replace('/', IOPath.DirectorySeparatorChar);

            if (!string.IsNullOrEmpty(overrideDirectory))
            {
                var local = IOPath.Combine(overrideDirectory, relative);

                if (!File.Exists(local))
                {
                    throw new FileNotFoundException(
                        $"Artifact '{name}' not found at '{local}' ({DirectoryVariable}={overrideDirectory}).", local);
                }

                Verify(name, entry, local, $"{DirectoryVariable} file '{local}'");

                return local;
            }

            var cached = IOPath.Combine(cacheRoot, manifest.Revision, relative);

            if (File.Exists(cached))
            {
                Verify(name, entry, cached, $"cache file '{cached}'");

                return cached;
            }

            var url = RawBaseUrl + manifest.Revision + "/" + entry.Path;
            var directory = IOPath.GetDirectoryName(cached);
            Directory.CreateDirectory(directory);

            var temp = IOPath.Combine(directory, IOPath.GetFileName(cached) + "." + Guid.NewGuid().ToString("n") + ".tmp");

            try
            {
                download(url, temp);
                Verify(name, entry, temp, $"download '{url}'");
                Publish(temp, cached);
            }
            finally
            {
                TryDelete(temp);
            }

            // A racing writer may have published first; its file must match as well.
            Verify(name, entry, cached, $"cache file '{cached}'");

            return cached;
        }

        internal static ArtifactManifest ParseManifest(string json, string source)
        {
            var doc = JsonSerializer.Deserialize(json).AsDocument;
            var revision = doc["revision"].AsString;

            if (revision == null || revision.Length != 40)
            {
                throw new InvalidDataException($"Manifest {source} must pin a 40-hex LiteDB-Artifacts revision.");
            }

            var files = new Dictionary<string, ArtifactEntry>(StringComparer.Ordinal);

            foreach (var file in doc["files"].AsDocument)
            {
                var item = file.Value.AsDocument;
                var path = item["path"].AsString;
                var sha256 = item["sha256"].AsString;

                if (string.IsNullOrEmpty(path) || sha256 == null || sha256.Length != 64)
                {
                    throw new InvalidDataException($"Manifest {source} entry '{file.Key}' needs a path and a 64-hex sha256.");
                }

                files[file.Key] = new ArtifactEntry(path, sha256.ToLowerInvariant());
            }

            return new ArtifactManifest(source, revision, files);
        }

        internal static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var hash = sha.ComputeHash(stream);

                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void Verify(string name, ArtifactEntry entry, string file, string source)
        {
            var actual = ComputeSha256(file);

            if (actual != entry.Sha256)
            {
                throw new InvalidDataException(
                    $"Artifact '{name}' hash mismatch from {source}: expected sha256 {entry.Sha256}, actual {actual}.");
            }
        }

        private static void Publish(string temp, string target)
        {
            try
            {
                File.Move(temp, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another caller published first; the caller re-verifies the winner.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void Download(string url, string target)
        {
#if NETFRAMEWORK
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = client.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new IOException(
                        $"Downloading '{url}' failed with HTTP {(int)response.StatusCode}. " +
                        $"Set {DirectoryVariable} to a local LiteDB-Artifacts checkout for offline runs.");
                }

                using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static ArtifactManifest LoadEmbeddedManifest()
        {
            using (var stream = typeof(ArtifactFixtures).Assembly.GetManifestResourceStream(ManifestResource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException($"Embedded manifest {ManifestResource} is missing.");
                }

                using (var reader = new StreamReader(stream))
                {
                    return ParseManifest(reader.ReadToEnd(), "LiteDB.Tests/Resources/artifacts.json");
                }
            }
        }
    }

    internal sealed class ArtifactManifest
    {
        public ArtifactManifest(string source, string revision, IDictionary<string, ArtifactEntry> files)
        {
            this.Source = source;
            this.Revision = revision;
            this.Files = files;
        }

        public string Source { get; }

        public string Revision { get; }

        public IDictionary<string, ArtifactEntry> Files { get; }
    }

    internal sealed class ArtifactEntry
    {
        public ArtifactEntry(string path, string sha256)
        {
            this.Path = path;
            this.Sha256 = sha256;
        }

        public string Path { get; }

        public string Sha256 { get; }
    }
}
