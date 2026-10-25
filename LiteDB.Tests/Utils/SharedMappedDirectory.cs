#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests
{
    /// <summary>Mapped-path tests require a qualified volume, independently of TMPDIR.</summary>
    internal static class SharedMappedDirectory
    {
        internal static readonly string Root;
        internal static readonly string SkipReason;

        static SharedMappedDirectory()
        {
            var configured = Environment.GetEnvironmentVariable("LITEDB_MAPPED_TEST_DIRECTORY");
            var candidates = string.IsNullOrWhiteSpace(configured)
                ? new[] { Path.GetTempPath(), AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
                : new[] { configured };
            var failures = new List<string>();
            foreach (var candidate in candidates.Distinct())
            {
                try
                {
                    var root = Path.GetFullPath(candidate);
                    var failure = SharedCoordinationPolicy.VolumeFailure(Path.Combine(root, "probe.db"));
                    if (failure != null) { failures.Add(failure); continue; }
                    Directory.CreateDirectory(root);
                    var probe = Path.Combine(root, ".litedb-test-probe-" + Guid.NewGuid().ToString("N"));
                    using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        1, FileOptions.DeleteOnClose)) { }
                    Root = root;
                    return;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                { failures.Add(candidate + ": " + error.Message); }
            }
            SkipReason = "Mapped Shared tests need a writable qualified filesystem. Set LITEDB_MAPPED_TEST_DIRECTORY " +
                "to a local ext2/3/4, XFS, Btrfs, NTFS, ReFS or APFS directory. " + string.Join("; ", failures);
        }
    }

    public sealed class MappedFactAttribute : FactAttribute
    {
        public MappedFactAttribute() { Skip = SharedMappedDirectory.SkipReason; }
    }

    public sealed class MappedTheoryAttribute : TheoryAttribute
    {
        public MappedTheoryAttribute() { Skip = SharedMappedDirectory.SkipReason; }
    }

    internal sealed class MappedTestFile : IDisposable
    {
        private readonly string _directory;
        internal string Filename { get; }
        internal MappedTestFile()
        {
            _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-mapped-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Filename = Path.Combine(_directory, "test.db");
        }
        public static implicit operator string(MappedTestFile file) => file.Filename;
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
#endif
