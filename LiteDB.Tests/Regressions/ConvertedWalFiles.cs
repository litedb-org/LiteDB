using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Regressions
{
    internal sealed class ConvertedWalFiles : IDisposable
    {
        private readonly TempFile _file = new TempFile();
        internal string Filename => _file.Filename;
        internal string LogName => FileHelper.GetLogFile(Filename);
        internal string Password { get; }
        internal byte[] Legacy { get; }
        internal byte[] Modern { get; }
        internal byte[] Wal { get; }

        internal ConvertedWalFiles(string password, byte legacyVersion = 8)
        {
            Password = password;
            Legacy = Entry("IndexMigration_5_0_21.zip", password == null ? "plain.db" : "encrypted.db");
            if (legacyVersion != 8)
                Legacy = Rewrite(Legacy, password, bytes => bytes[HeaderPage.P_FILE_VERSION] = legacyVersion);
            File.WriteAllBytes(Filename, Legacy);
            using (var db = Open())
            {
                db.CheckpointSize = 0;
                db.Checkpoint();
                var rows = db.GetCollection("rows");
                rows.Update(rows.FindAll().First());
                Wal = ReadShared(LogName);
                Modern = ReadShared(Filename);
            }
            var frame = Plaintext(Wal, password).Take(WalChecksum.FrameSize).ToArray();
            WalChecksum.IsFrame(frame, 0).Should().BeTrue();
            frame[BasePage.P_PAGE_FORMAT].Should().Be(PageChecksum.Checksummed);
            frame[BasePage.P_IS_CONFIRMED].Should().Be(1);
            BitConverter.ToUInt32(frame, BasePage.P_PAGE_ID).Should().Be(3);
        }

        internal LiteDatabase Open(bool readOnly = false) => new LiteDatabase(new ConnectionString
        {
            Filename = Filename, Password = Password, ReadOnly = readOnly, LegacyIndexScan = readOnly
        });

        internal byte[] Damage(string damage)
        {
            if (damage == "incomplete-page") return Truncate(Constants.PAGE_SIZE - 1);
            if (damage == "absent-trailer") return Truncate(Constants.PAGE_SIZE);
            if (damage == "partial-trailer") return Truncate(Constants.PAGE_SIZE + 32);
            if (damage == "crc") return Rewrite(Wal, Password, bytes => bytes[Constants.PAGE_SIZE + 60] ^= 1);
            if (damage == "payload") return Rewrite(Wal, Password, bytes => bytes[100] ^= 1);
            if (damage == "format") return Rewrite(Wal, Password, bytes => bytes[BasePage.P_PAGE_FORMAT] = 0);
            if (damage == "intact") return (byte[])Wal.Clone();
            throw new ArgumentOutOfRangeException(nameof(damage));
        }

        internal byte[] Truncate(int length) => Wal.Take((Password == null ? 0 : Constants.PAGE_SIZE) + length).ToArray();

        internal void Install(byte[] data, byte[] wal)
        {
            File.WriteAllBytes(Filename, data);
            File.WriteAllBytes(LogName, wal);
        }

        internal static byte[] Entry(string archive, string name)
        {
            using var resource = typeof(ConvertedWalFiles).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources." + archive);
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }

        internal static byte[] Plaintext(byte[] bytes, string password)
        {
            using var raw = new MemoryStream(bytes);
            using var factory = new StreamFactory(raw, password);
            using var stream = factory.GetStream(false, false);
            using var result = new MemoryStream();
            stream.CopyTo(result);
            return result.ToArray();
        }

        internal static byte[] Rewrite(byte[] bytes, string password, Action<byte[]> change)
        {
            var plain = Plaintext(bytes, password);
            change(plain);
            using var raw = new MemoryStream();
            raw.Write(bytes, 0, bytes.Length);
            using (var factory = new StreamFactory(raw, password))
            using (var stream = factory.GetStream(true, false)) stream.Write(plain, 0, plain.Length);
            return raw.ToArray();
        }

        private static byte[] ReadShared(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var result = new MemoryStream();
            stream.CopyTo(result);
            return result.ToArray();
        }

        public void Dispose()
        {
            File.Delete(LogName);
            _file.Dispose();
        }
    }
}
