using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    internal static class MvccRootedCheckpointScenario
    {
        internal static void Run(string password, bool compact, string phase, string fault, int prefix = 59,
            bool repeatRepair = false)
        {
            MvccRetirementScenario.Run(password, compact, null, priorReclaims: 1, inspect: (dataBytes, logBytes) =>
            {
                ReadRoot(dataBytes, password).Should().BeGreaterThan(0, "the fault must start with a published retirement chain");
                using var data = new PromotionPowerLossStream(dataBytes);
                using var log = new PromotionPowerLossStream(logBytes);
                var fired = false;
                var armed = false;
                Action cut = () =>
                {
                    fired = true;
                    data.PowerCut();
                    log.PowerCut();
                    throw new IOException("Rooted checkpoint power loss");
                };
                using (var engine = new LiteEngine(Settings(data, log, password, compact)))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    MvccRetirementScenario.VerifyDatabase(db);
                    Action<string> crash = current =>
                    {
                        if (current != phase || armed) return;
                        armed = true;
                        if (fault == "cut") cut();
                        var target = phase == "checkpoint-before-clear" || phase == "before-commit-lock" ? log : data;
                        if (fault == "flush") target.BeforeFlush = cut;
                        else if (fault == "journal-flush")
                        {
                            var flushes = 0;
                            // The first sync protects initialized WAL padding;
                            // the second seals the recovery header and WAL binding.
                            target.BeforeFlush = () => { if (++flushes == 2) cut(); };
                        }
                        else if (fault == "tear")
                        {
                            target.TearNextWrite = (bytes, offset, count) =>
                            {
                                fired = true;
                                target.TearWrite(bytes, offset, count, prefix, damage: true);
                                data.PowerCut();
                                log.PowerCut();
                            };
                        }
                    };
                    try
                    {
                        engine.CheckpointStage = crash;
                        EngineState.SimulateProcessCrash = crash;
                        Action checkpoint = () => db.Checkpoint();
                        checkpoint.Should().Throw<IOException>();
                        fired.Should().BeTrue("the requested full-checkpoint failure must execute");
                    }
                    finally
                    {
                        engine.CheckpointStage = null;
                        EngineState.SimulateProcessCrash = null;
                    }
                }

                var savedData = data.DurableBytes;
                var savedLog = log.DurableBytes;
                if (repeatRepair)
                    for (var attempt = 0; attempt < 2; attempt++)
                        TearRepair(ref savedData, ref savedLog, password, compact);
                MvccRetirementScenario.Verify(savedData, savedLog, password);
                VerifyRootRemoval(savedData, savedLog, password, compact);
            });
        }

        private static void TearRepair(ref byte[] dataBytes, ref byte[] logBytes, string password, bool compact)
        {
            using var data = new PromotionPowerLossStream(dataBytes);
            using var log = new PromotionPowerLossStream(logBytes);
            var fired = false;
            try
            {
                EngineState.SimulateProcessCrash = phase =>
                {
                    if (phase != "promotion-recovery-before-header-write") return;
                    data.TearNextWrite = (bytes, offset, count) =>
                    {
                        fired = true;
                        data.TearWrite(bytes, offset, count, 171, damage: true);
                        log.PowerCut();
                    };
                };
                Action open = () =>
                {
                    using var engine = new LiteEngine(Settings(data, log, password, compact));
                };
                open.Should().Throw<IOException>();
                fired.Should().BeTrue("the old rooted header must be repaired from the checkpoint journal");
            }
            finally { EngineState.SimulateProcessCrash = null; }
            dataBytes = data.DurableBytes;
            logBytes = log.DurableBytes;
        }

        private static void VerifyRootRemoval(byte[] dataBytes, byte[] logBytes, string password, bool compact)
        {
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = ChecksumTestFiles.Copy(logBytes);
            using (var engine = new LiteEngine(Settings(data, log, password, compact)))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                MvccRetirementScenario.VerifyDatabase(db);
                var rootedBefore = ReadRoot(data.ToArray(), password) != 0;
                var saltBefore = ReadSalt(data.ToArray(), password);
                db.Checkpoint();
                ReadRoot(data.ToArray(), password).Should().Be(0);
                if (rootedBefore) ReadSalt(data.ToArray(), password).Should().NotEqual(saltBefore);
                using var logSource = ChecksumTestFiles.Copy(log.ToArray());
                using var factory = new StreamFactory(logSource, password);
                using var plainLog = factory.GetStream(false, false);
                plainLog.Length.Should().Be(0, "full checkpoint must retire the root and its WAL generation together");
                MvccRetirementScenario.VerifyDatabase(db);
            }
            MvccRetirementScenario.Verify(data.ToArray(), log.ToArray(), password);
        }

        private static long ReadRoot(byte[] bytes, string password)
        {
            var header = ReadHeader(bytes, password);
            header[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.MVCC_FILE_VERSION);
            return new BufferSlice(header, 0, PAGE_SIZE).ReadInt64(WalRetirement.RootPosition);
        }

        private static byte[] ReadSalt(byte[] bytes, string password)
        {
            var salt = new byte[16];
            Buffer.BlockCopy(ReadHeader(bytes, password), WalChecksum.SaltPosition, salt, 0, salt.Length);
            return salt;
        }

        private static byte[] ReadHeader(byte[] bytes, string password)
        {
            using var source = ChecksumTestFiles.Copy(bytes);
            using var factory = new StreamFactory(source, password);
            using var stream = factory.GetStream(false, false);
            var header = new byte[PAGE_SIZE];
            stream.ReadRequired(header, 0, header.Length);
            return header;
        }

        private static EngineSettings Settings(Stream data, Stream log, string password, bool compact) => new EngineSettings
        {
            DataStream = data, LogStream = log, Password = password,
            CompactStorage = compact ? CompactStorageMode.Auto : CompactStorageMode.Legacy
        };
    }
}
