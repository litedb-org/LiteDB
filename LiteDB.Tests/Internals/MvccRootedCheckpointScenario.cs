using System;
using System.IO;
using System.Linq;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    internal static class MvccRootedCheckpointScenario
    {
        internal static void Run(string password, bool compact, string phase, string fault, int prefix = 59,
            bool repeatRepair = false, Action<byte[], byte[]> inspect = null, int[] repairPrefixes = null)
        {
            MvccRetirementScenario.Run(password, compact, null, priorReclaims: 1, inspect: (dataBytes, logBytes) =>
            {
                inspect?.Invoke(dataBytes, logBytes);
                Require(ReadRoot(dataBytes, password) > 0, "The fault must start with a published retirement chain");
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
                        ExpectIOException(() => db.Checkpoint());
                        Require(fired, "The requested full-checkpoint failure must execute");
                    }
                    finally
                    {
                        engine.CheckpointStage = null;
                        EngineState.SimulateProcessCrash = null;
                        inspect?.Invoke(data.DurableBytes, log.DurableBytes);
                    }
                }

                var savedData = data.DurableBytes;
                var savedLog = log.DurableBytes;
                if (repeatRepair)
                    for (var attempt = 0; attempt < 2; attempt++)
                        TearRepair(ref savedData, ref savedLog, password, compact, repairPrefixes?[attempt] ?? 171, inspect);
                MvccRetirementScenario.Verify(savedData, savedLog, password);
                VerifyRootRemoval(savedData, savedLog, password, compact);
            });
        }

        private static void TearRepair(ref byte[] dataBytes, ref byte[] logBytes, string password, bool compact,
            int prefix, Action<byte[], byte[]> inspect)
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
                        data.TearWrite(bytes, offset, count, prefix, damage: true);
                        log.PowerCut();
                    };
                };
                ExpectIOException(() =>
                {
                    using var engine = new LiteEngine(Settings(data, log, password, compact));
                });
                Require(fired, "The old rooted header must be repaired from the checkpoint journal");
            }
            finally
            {
                EngineState.SimulateProcessCrash = null;
                inspect?.Invoke(data.DurableBytes, log.DurableBytes);
            }
            dataBytes = data.DurableBytes;
            logBytes = log.DurableBytes;
        }

        private static void VerifyRootRemoval(byte[] dataBytes, byte[] logBytes, string password, bool compact)
        {
            using var data = Copy(dataBytes);
            using var log = Copy(logBytes);
            using (var engine = new LiteEngine(Settings(data, log, password, compact)))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                MvccRetirementScenario.VerifyDatabase(db);
                var rootedBefore = ReadRoot(data.ToArray(), password) != 0;
                var saltBefore = ReadSalt(data.ToArray(), password);
                db.Checkpoint();
                Require(ReadRoot(data.ToArray(), password) == 0, "Full checkpoint retained the retirement root");
                if (rootedBefore) Require(!ReadSalt(data.ToArray(), password).SequenceEqual(saltBefore), "Full checkpoint did not rotate the WAL salt");
                using var logSource = Copy(log.ToArray());
                using var factory = new StreamFactory(logSource, password);
                using var plainLog = factory.GetStream(false, false);
                Require(plainLog.Length == 0, "Full checkpoint must retire the root and its WAL generation together");
                MvccRetirementScenario.VerifyDatabase(db);
            }
            MvccRetirementScenario.Verify(data.ToArray(), log.ToArray(), password);
        }

        private static long ReadRoot(byte[] bytes, string password)
        {
            var header = ReadHeader(bytes, password);
            Require(header[HeaderPage.P_FILE_VERSION] == HeaderPage.MVCC_FILE_VERSION, "Expected a v13 header");
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
            using var source = Copy(bytes);
            using var factory = new StreamFactory(source, password);
            using var stream = factory.GetStream(false, false);
            var header = new byte[PAGE_SIZE];
            stream.ReadRequired(header, 0, header.Length);
            return header;
        }

        private static MemoryStream Copy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private static void ExpectIOException(Action action)
        {
            try { action(); }
            catch (IOException) { return; }
            throw new InvalidOperationException("The injected checkpoint or repair failure did not throw IOException");
        }

        private static EngineSettings Settings(Stream data, Stream log, string password, bool compact) => new EngineSettings
        {
            DataStream = data, LogStream = log, Password = password,
            CompactStorage = compact ? CompactStorageMode.Auto : CompactStorageMode.Legacy
        };
    }
}
