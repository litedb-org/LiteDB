using System;
using System.IO;
using LiteDB.Engine;
using LiteDB.Internals;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    internal static class IndexMigrationFixtures
    {
        internal static void Rewrite(string filename, string password, Action<byte[]> change, bool changeData = true, bool changeWal = true)
        {
            using var data = ChecksumTestFiles.Copy(File.ReadAllBytes(filename));
            var logName = FileHelper.GetLogFile(filename);
            using var log = ChecksumTestFiles.Copy(File.Exists(logName) ? File.ReadAllBytes(logName) : Array.Empty<byte>());
            Rewrite(data, log, password, change, changeData, changeWal);
            File.WriteAllBytes(filename, data.ToArray());
            if (log.Length != 0 || File.Exists(logName)) File.WriteAllBytes(logName, log.ToArray());
        }

        internal static void Rewrite(MemoryStream data, MemoryStream log, string password, Action<byte[]> change, bool changeData = true, bool changeWal = true)
        {
            var bytes = new byte[PAGE_SIZE];
            using (var factory = new StreamFactory(data, password))
            using (var stream = factory.GetStream(false, false)) stream.ReadRequired(bytes, 0, PAGE_SIZE);
            var current = bytes[HeaderPage.P_FILE_VERSION];
            if (changeData) change(bytes);
            var legacy = bytes[HeaderPage.P_FILE_VERSION] < HeaderPage.CHECKSUM_FILE_VERSION;
            if (legacy && current >= HeaderPage.CHECKSUM_FILE_VERSION)
                ChecksumTestFiles.MakeLegacy(data, log, password, bytes[HeaderPage.P_FILE_VERSION]);
            using (var factory = new StreamFactory(data, password))
            using (var stream = factory.GetStream(true, false))
            {
                stream.ReadRequired(bytes, 0, PAGE_SIZE);
                if (changeData) change(bytes);
                if (!legacy) PageChecksum.Write(new BufferSlice(bytes, 0, PAGE_SIZE));
                stream.Position = 0;
                stream.Write(bytes, 0, PAGE_SIZE);
            }
            if (log.Length == 0) return;
            var checksums = new WalChecksum();
            var salt = new byte[16];
            Buffer.BlockCopy(bytes, WalChecksum.SaltPosition, salt, 0, salt.Length);
            checksums.Reset(salt);
            using (var factory = new StreamFactory(log, password))
            using (var stream = factory.GetStream(true, false))
            {
                var size = legacy ? PAGE_SIZE : WalChecksum.FrameSize;
                var frame = new byte[size];
                for (long position = 0; position + size <= stream.Length; position += size)
                {
                    stream.Position = position;
                    stream.ReadRequired(frame, 0, size);
                    var page = new BufferSlice(frame, 0, PAGE_SIZE);
                    if (changeWal && frame[BasePage.P_PAGE_TYPE] == (byte)PageType.Header) change(frame);
                    if (!legacy)
                    {
                        var logical = position / size * PAGE_SIZE;
                        var prepared = checksums.Prepare(page, new BufferSlice(frame, PAGE_SIZE, WalChecksum.MetadataSize), logical);
                        checksums.Accept(page, logical, prepared);
                    }
                    stream.Position = position;
                    stream.Write(frame, 0, size);
                }
            }
        }
    }
}
