using System.IO;
using System.IO.Compression;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Files written by the released LiteDB 5.0.21 package (format v8, en-US/None,
    /// checkpointed, no WAL). "stale" holds index keys that released updates kept although
    /// the document changed; "indexes" holds every index shape whose v11 order differs.
    /// Each has a plain and an "-encrypted" (password "secret") variant.
    /// </summary>
    internal static class LegacyIndexFixtures
    {
        internal const string Password = "secret";

        internal static TempFile Extract(string name, string password)
        {
            var entry = name + (password == null ? "" : "-encrypted") + ".db";
            using var resource = typeof(LegacyIndexFixtures).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.IndexMigrationLegacyKeys_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            var file = new TempFile();
            using (var source = zip.GetEntry(entry).Open())
            using (var target = File.Create(file.Filename))
                source.CopyTo(target);
            return file;
        }
    }
}
