using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class LegacyWalCallerStream_Tests
    {
        public static IEnumerable<object[]> NonWritableStreams()
        {
            foreach (var encrypted in new[] { false, true })
            foreach (var directEngine in new[] { false, true })
            foreach (var partialTail in new[] { false, true })
            foreach (var writable in new[] { (false, false), (false, true), (true, false) })
                if (!encrypted || directEngine) // The public stream constructor has no password option.
                    yield return new object[] { encrypted, directEngine, partialTail, writable.Item1, writable.Item2 };
        }

        // S07 must select byte-preserving legacy reads before S06 attempts its destructive drain.
        // A caller log does not permit writes while the legacy indexes still need migration.
        [Theory]
        [MemberData(nameof(NonWritableStreams))]
        public void Non_writable_legacy_pair_preserves_commits_and_refuses_new_writes(
            bool encrypted, bool directEngine, bool partialTail, bool writableData, bool writableLog)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            var password = encrypted ? "wal-secret" : null;
            var originalData = Entry(encrypted, "crash.db");
            var originalLog = Entry(encrypted, "crash-log.db");
            if (partialTail)
            {
                originalData = originalData.Concat(new byte[100]).ToArray();
                originalLog = originalLog.Concat(new byte[100]).ToArray();
            }
            File.WriteAllBytes(dataFile.Filename, originalData);
            File.WriteAllBytes(logFile.Filename, originalLog);

            // The released fixture's committed state, independent of the candidate migration.
            var expected = Expected(encrypted);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using (var data = Open(dataFile, writableData))
                using (var log = Open(logFile, writableLog))
                {
                    var settings = new EngineSettings
                    {
                        DataStream = data, LogStream = log, Password = password,
                        // A blocked S06 drain must not supersede S07's non-writable fallback.
                        SharedReaderVersions = () => null
                    };
                    using (var db = directEngine ? new LiteDatabase(new LiteEngine(settings)) : new LiteDatabase(data, null, log))
                    {
                        AssertDocuments(db, expected);
                        db.BeginTrans().Should().BeTrue();
                        AssertDocuments(db, expected);
                        db.GetCollection("docs").Invoking(rows => rows.Insert(new BsonDocument { ["_id"] = 1000, ["value"] = 9 }))
                            .Should().Throw<IOException>().WithMessage("*read-only*");
                        db.Commit().Should().BeTrue("the refused write must leave a read transaction usable");
                        db.BeginTrans().Should().BeTrue();
                        db.Rollback().Should().BeTrue();
                        db.Invoking(x => x.Checkpoint()).Should().NotThrow("S07 keeps checkpoint a byte-preserving no-op on non-writable storage");
                        AssertDocuments(db, expected);
                    }
                    data.CanRead.Should().BeTrue("the caller owns both streams, including after fallback cleanup");
                    log.CanRead.Should().BeTrue();
                    settings.ReadOnly.Should().BeFalse("fallback flags belong to the engine's settings copy");
                }
                File.ReadAllBytes(dataFile.Filename).Should().Equal(originalData);
                File.ReadAllBytes(logFile.Filename).Should().Equal(originalLog);
            }

            // Retry the exact retained files writable, then reopen twice: the WAL-only commits
            // must survive conversion and the new acknowledged insert, while rollback stays absent.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var data = Open(dataFile, true);
                using var log = Open(logFile, true);
                using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password })))
                {
                    if (attempt == 0)
                    {
                        AssertDocuments(db, expected);
                        db.BeginTrans().Should().BeTrue();
                        db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1000, ["value"] = 9 });
                        db.Commit().Should().BeTrue();
                        db.BeginTrans().Should().BeTrue();
                        db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1001, ["value"] = 10 });
                        // Dispose rolls back 1001 while retaining the earlier acknowledged commit.
                    }
                    else AssertDocuments(db, expected.Concat(new[] { new BsonDocument { ["_id"] = 1000, ["value"] = 9 } }).ToArray());
                }
                data.CanWrite.Should().BeTrue();
                log.CanWrite.Should().BeTrue();
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Foreign_legacy_wal_is_refused_without_mutation_even_when_streams_cannot_be_written(bool writableData, bool writableLog)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            var dataBytes = Entry(false, "crash.db");
            using (var zip = new ZipArchive(File.OpenRead(ArtifactFixtures.Path("ForeignWal_5_0_21.zip")), ZipArchiveMode.Read))
            using (var entry = zip.GetEntry("foreign-log.db").Open())
            using (var output = File.Create(logFile.Filename)) entry.CopyTo(output);
            File.WriteAllBytes(dataFile.Filename, dataBytes);
            var logBytes = File.ReadAllBytes(logFile.Filename);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var data = Open(dataFile, writableData);
                using var log = Open(logFile, writableLog);
                Action open = () => new LiteDatabase(data, null, log).Dispose();
                open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INVALID_DATABASE)
                    .WithMessage("*commits the header of another database*");
                data.CanRead.Should().BeTrue();
                log.CanRead.Should().BeTrue();
                File.ReadAllBytes(dataFile.Filename).Should().Equal(dataBytes);
                File.ReadAllBytes(logFile.Filename).Should().Equal(logBytes);
            }
        }

        private static FileStream Open(TempFile file, bool writable) => new FileStream(file.Filename,
            FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);

        private static BsonDocument[] Expected(bool encrypted) => Enumerable.Range(0, encrypted ? 65 : 101)
            .Select(id => new BsonDocument { ["_id"] = id, ["value"] = (encrypted ? id < 10 || id >= 60 : id < 20 || id == 100) ? 7 : 0 })
            .ToArray();

        private static void AssertDocuments(LiteDatabase db, BsonDocument[] expected)
        {
            var rows = db.GetCollection("docs");
            rows.FindAll().OrderBy(x => x["_id"]).Select(x => JsonSerializer.Serialize(x))
                .Should().Equal(expected.OrderBy(x => x["_id"]).Select(x => JsonSerializer.Serialize(x)));
            foreach (var value in new[] { 0, 7, 9, 10 })
                rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).OrderBy(x => x)
                    .Should().Equal(expected.Where(x => x["value"].AsInt32 == value).Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }

        private static byte[] Entry(bool encrypted, string name)
        {
            using var zip = new ZipArchive(File.OpenRead(ArtifactFixtures.Path(
                encrypted ? "EncryptedWalCrash_5_0_21.zip" : "WalCrash_5_0_21.zip")), ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
