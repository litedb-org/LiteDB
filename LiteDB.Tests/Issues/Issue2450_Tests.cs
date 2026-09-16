using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2450_Tests
    {
        private const string SentinelContents = "unrelated same-prefix backup: retain exactly";

        [Fact]
        public void Repeated_rebuild_keeps_one_current_readable_backup_and_preserves_unrelated_files()
        {
            ExerciseRepeatedRebuild(checkpointBeforeRebuild: true);
        }

        [Fact]
        public void Repeated_rebuild_with_live_wal_consolidates_commits_into_the_current_backup()
        {
            ExerciseRepeatedRebuild(checkpointBeforeRebuild: false);
        }

        private static void ExerciseRepeatedRebuild(bool checkpointBeforeRebuild)
        {
            var mode = checkpointBeforeRebuild ? "checkpointed" : "live-wal";
            var directory = Path.Combine(
                Path.GetTempPath(),
                "litedb-2450-" + mode + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var file = Path.Combine(directory, "data.db");
            var log = Path.Combine(directory, "data-log.db");
            var dataBackup = Path.Combine(directory, "data-backup.db");
            var sentinel = Path.Combine(directory, "data-backup-manual.db");
            File.WriteAllText(sentinel, SentinelContents);

            byte[] previousBackupGeneration = null;

            try
            {
                for (var generation = 1; generation <= 3; generation++)
                {
                    using (var db = new LiteDatabase(file))
                    {
                        if (!checkpointBeforeRebuild)
                        {
                            db.CheckpointSize = 0;
                        }

                        db.GetCollection("rows").Insert(CreateRow(generation));

                        if (checkpointBeforeRebuild)
                        {
                            db.Checkpoint();
                        }
                        else
                        {
                            db.CheckpointSize.Should().Be(0);
                            File.Exists(log).Should().BeTrue("the newest committed generation must still have a WAL");
                            new FileInfo(log).Length.Should().BeGreaterThan(0);
                        }

                        db.Rebuild();
                    }

                    AssertExactInventory(directory);
                    File.ReadAllText(sentinel).Should().Be(SentinelContents,
                        "cleanup may replace LiteDB-owned backups but not a same-prefix user file");

                    var expectedRows = Enumerable.Range(1, generation).ToArray();
                    AssertRowsInFile(file, expectedRows);

                    var dataBackupBytes = File.ReadAllBytes(dataBackup);
                    // Rebuild checkpoints before atomic replacement, so the backup
                    // is now independently complete even when CHECKPOINT was zero.
                    AssertRowsInStreams(dataBackupBytes, Array.Empty<byte>(), expectedRows);
                    var backupGeneration = dataBackupBytes;
                    if (previousBackupGeneration != null)
                    {
                        backupGeneration.Should().NotEqual(previousBackupGeneration,
                            "the single canonical backup must be replaced with the latest generation");
                    }
                    previousBackupGeneration = backupGeneration;

                    AssertExactInventory(directory);
                    File.ReadAllText(sentinel).Should().Be(SentinelContents);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static BsonDocument CreateRow(int generation)
        {
            return new BsonDocument
            {
                ["_id"] = generation,
                ["generation"] = generation,
                ["ledger"] = "generation-" + generation + "-check-" + (generation * 37)
            };
        }

        private static void AssertExactInventory(string directory)
        {
            var expected = new[]
            {
                "data-backup-manual.db",
                "data-backup.db",
                "data.db"
            };

            Directory.GetFiles(directory)
                .Select(Path.GetFileName)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Should().Equal(expected);
        }

        private static void AssertRowsInFile(string filename, IReadOnlyCollection<int> generations)
        {
            using var db = new LiteDatabase(filename);
            AssertRows(db, generations);
        }

        private static void AssertRowsInStreams(
            byte[] dataBytes,
            byte[] logBytes,
            IReadOnlyCollection<int> generations)
        {
            using var data = CopyToExpandableStream(dataBytes);
            using var log = CopyToExpandableStream(logBytes);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine);
            AssertRows(db, generations);
        }

        private static MemoryStream CopyToExpandableStream(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        private static void AssertRows(LiteDatabase db, IReadOnlyCollection<int> generations)
        {
            var rows = db.GetCollection("rows").FindAll()
                .OrderBy(x => x["_id"].AsInt32)
                .ToArray();

            rows.Select(x => x["_id"].AsInt32).Should().Equal(generations);
            rows.Select(x => x["generation"].AsInt32).Should().Equal(generations);
            rows.Select(x => x["ledger"].AsString).Should().Equal(
                generations.Select(x => "generation-" + x + "-check-" + (x * 37)));
        }
    }
}
