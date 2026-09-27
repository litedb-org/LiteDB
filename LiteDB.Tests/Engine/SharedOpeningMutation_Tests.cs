using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("PromotionPowerLoss")]
    public class SharedOpeningMutation_Tests
    {
        [Theory]
        [InlineData(null, "clean")]
        [InlineData("secret", "clean")]
        [InlineData(null, "new")]
        [InlineData("secret", "new")]
        [InlineData(null, "legacy")]
        [InlineData("secret", "legacy")]
        [InlineData(null, "data-tail")]
        [InlineData("secret", "data-tail")]
        [InlineData(null, "wal-tail")]
        [InlineData("secret", "wal-tail")]
        public void Startup_mutations_have_a_fence_and_a_clean_open_has_none(string password, string state)
        {
            WithFiles((file, logFile) =>
            {
                if (state != "new") Seed(file, password);
                if (state == "legacy")
                {
                    using var data = ChecksumTestFiles.Copy(File.ReadAllBytes(file));
                    using var log = new MemoryStream();
                    ChecksumTestFiles.MakeLegacy(data, log, password);
                    File.WriteAllBytes(file, data.ToArray());
                }
                if (state == "data-tail" || state == "wal-tail")
                {
                    var path = state == "data-tail" ? file : logFile;
                    // Create an encrypted WAL header through its real stream wrapper.
                    if (state == "wal-tail" && password != null)
                    {
                        using var factory = new FileStreamFactory(path, password, false, false);
                        using var stream = factory.GetStream(true, false);
                    }
                    using var tail = new FileStream(path, FileMode.Append, FileAccess.Write);
                    tail.Write(new byte[17], 0, 17);
                }
                var before = File.Exists(file) ? File.ReadAllBytes(file) : null;
                var guard = new OpeningStorageGuard();
                using (var data = guard.Open(file, true))
                using (var log = guard.Open(logFile, false))
                {
                    var engine = new LiteEngine(new EngineSettings
                    {
                        DataStream = data, LogStream = log, Password = password,
                        CoordinationSignals = guard
                    });
                    guard.Depth.Should().Be(0);
                    if (state == "clean")
                    {
                        guard.Begins.Should().Be(0);
                        guard.Mutations.Should().Be(0);
                    }
                    else guard.Mutations.Should().BeGreaterThan(0, state + " must reach a real opening mutation");
                    guard.Armed = false;
                    using (engine)
                    using (var database = new LiteDatabase(engine, disposeOnClose: false))
                    {
                        if (state == "new") Populate(database);
                        Verify(database);
                        engine.Close(checkpoint: false);
                    }
                }
                if (state == "clean") File.ReadAllBytes(file).Should().Equal(before);
                using (var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password })) Verify(cold);
            });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Automatic_rebuild_fences_admission_before_the_external_lease_scan(string password)
        {
            WithFiles((file, _) =>
            {
                Seed(file, password);
                using (var factory = new FileStreamFactory(file, password, false, false))
                using (var stream = factory.GetStream(true, false))
                {
                    var header = new byte[Constants.PAGE_SIZE];
                    stream.ReadRequired(header, 0, header.Length);
                    header[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
                    PageChecksum.Write(new BufferSlice(header, 0, header.Length));
                    stream.Position = 0;
                    stream.Write(header, 0, header.Length);
                    stream.FlushToDisk();
                }
                var before = File.ReadAllBytes(file);
                var guard = new OpeningStorageGuard();
                var scanned = false;
                using (var engine = new LiteEngine(new EngineSettings
                {
                    Filename = file, Password = password, AutoRebuild = true, CoordinationSignals = guard,
                    AutoRebuildAllowed = () =>
                    {
                        guard.Depth.Should().BeGreaterThan(0, "the lease scan must follow admission exclusion");
                        scanned = true;
                        return false;
                    }
                }))
                using (var database = new LiteDatabase(engine, disposeOnClose: false))
                {
                    Verify(database);
                    engine.Close(checkpoint: false);
                }
                scanned.Should().BeTrue();
                guard.Depth.Should().Be(0);
                File.ReadAllBytes(file).Should().Equal(before);
                Directory.GetFiles(Path.GetDirectoryName(file), "*backup*").Should().BeEmpty();
            });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Repeated_torn_header_repairs_remain_fenced_and_preserve_recovery_evidence(string password)
        {
            PromotionPowerLossScenario.Run(password, false, "promotion-before-header-write",
                tornPrefix: 59, damage: true, inspectFiles: (dataBytes, logBytes) => WithFiles((file, logFile) =>
                {
                    File.WriteAllBytes(file, dataBytes);
                    File.WriteAllBytes(logFile, logBytes);
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        var guard = new OpeningStorageGuard { TearDataWrite = attempt < 2 };
                        using (var data = guard.Open(file, true))
                        using (var log = guard.Open(logFile, false))
                        {
                            var settings = new EngineSettings
                            {
                                DataStream = data, LogStream = log, Password = password, CoordinationSignals = guard
                            };
                            if (attempt < 2)
                            {
                                Action open = () => { using var failed = new LiteEngine(settings); };
                                open.Should().Throw<IOException>().WithMessage("injected opening write tear");
                            }
                            else
                            {
                                using var engine = new LiteEngine(settings);
                                guard.Armed = false;
                                using var database = new LiteDatabase(engine, disposeOnClose: false);
                                PromotionPowerLossScenario.Verify(database, false, false);
                                engine.Close(checkpoint: false);
                            }
                            guard.Depth.Should().Be(0);
                            guard.Mutations.Should().BeGreaterThan(0);
                        }
                        if (attempt < 2) File.ReadAllBytes(logFile).Should().Equal(logBytes);
                    }
                    using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                    PromotionPowerLossScenario.Verify(cold, false, false);
                }));
        }

        private static void Seed(string file, string password)
        {
            using var database = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            Populate(database);
        }

        private static void Populate(LiteDatabase database)
        {
            var rows = database.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(0, 32).Select(id => new BsonDocument
                { ["_id"] = id, ["key"] = id * 13, ["payload"] = new string('z', 1000) }));
            rows.EnsureIndex("key");
        }

        private static void Verify(LiteDatabase database)
        {
            var rows = database.GetCollection("rows");
            rows.FindAll().OrderBy(row => row["_id"].AsInt32).Select(row => row["_id"].AsInt32)
                .Should().Equal(Enumerable.Range(0, 32));
            for (var id = 0; id < 32; id++)
            {
                var row = rows.Find(Query.EQ("key", id * 13)).Single();
                row["_id"].AsInt32.Should().Be(id);
                row["payload"].AsString.Should().Be(new string('z', 1000));
            }
        }

        private static void WithFiles(Action<string, string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-opening-mutation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "test.db");
            test(file, FileHelper.GetLogFile(file));
            Directory.Delete(directory, true); // Preserve failures for inspection.
        }
    }
}
