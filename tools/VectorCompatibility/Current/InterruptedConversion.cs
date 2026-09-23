using System;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Engine;

namespace VectorCompatibility.Current
{
    internal static class InterruptedConversion
    {
        // Logical WAL slots of the header-only conversion's redo and footer descriptor.
        internal static readonly (string Name, long Slot)[] UnsealedStops = { ("redo", 8192), ("descriptor", 3 * 8192) };

        internal static void Create(string directory, string suffix, string password)
        {
            var file = Path.Combine(directory, "interrupted-" + suffix);
            File.Copy(Path.Combine(directory, "legacy-" + suffix), file);
            using var data = new PublicationFailure(file, password == null ? 0 : 8192);
            using var log = new FileStream(Path.ChangeExtension(file, null) + "-log.db", FileMode.Create, FileAccess.ReadWrite);
            try
            {
                using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
                throw new Exception("Conversion must stop before publishing v10");
            }
            catch (IOException ex) when (ex.Message == "interrupted conversion") { }
        }

        /// <summary>Stops conversion inside the WAL, before its footer is sealed.</summary>
        internal static void CreateUnsealed(string directory, string suffix, string password)
        {
            foreach (var (name, slot) in UnsealedStops)
            {
                var file = Path.Combine(directory, "unsealed-" + name + "-" + suffix);
                File.Copy(Path.Combine(directory, "legacy-" + suffix), file);
                using var data = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);
                using var log = new WalFailure(Path.ChangeExtension(file, null) + "-log.db", (password == null ? 0 : 8192) + slot);
                try
                {
                    using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
                    throw new Exception("Conversion must stop before sealing its footer");
                }
                catch (IOException ex) when (ex.Message == "interrupted conversion") { }
            }
        }

        /// <summary>
        /// The released engine appended commits after the unsealed records, with
        /// and without its own checkpoint. Reading must preserve both files;
        /// writable opens must keep those commits and finish the conversion.
        /// </summary>
        internal static void VerifyUnsealed(string directory, string suffix, string password)
        {
            foreach (var (name, _) in UnsealedStops)
            foreach (var prefix in new[] { "unsealed-", "unsealed-wal-" })
            {
                var file = Path.Combine(directory, prefix + name + "-" + suffix);
                var log = Path.ChangeExtension(file, null) + "-log.db";
                var original = File.ReadAllBytes(file);
                var originalLog = File.Exists(log) ? File.ReadAllBytes(log) : null;
                foreach (var readOnly in new[] { true, false })
                {
                    using (var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password, ReadOnly = readOnly }))
                    {
                        var docs = db.GetCollection("docs");
                        if (docs.Count() != 2 || docs.FindById(1)["value"].AsString != "legacy" || docs.FindById(2)["value"].AsString != "resumed")
                            throw new Exception("Unsealed conversion lost a released-engine commit: " + prefix + name);
                        using var info = db.Execute("SELECT $ FROM $database");
                        var coverage = info.Read() ? info.Current["checksumCoverage"].AsString : null;
                        if (coverage != (readOnly ? "Legacy" : "Mixed")) throw new Exception("Unexpected checksum coverage " + coverage);
                    }
                    if (readOnly && (!original.SequenceEqual(File.ReadAllBytes(file)) ||
                        (originalLog != null && !originalLog.SequenceEqual(File.ReadAllBytes(log)))))
                        throw new Exception("Read-only recovery must preserve the files: " + prefix + name);
                }
            }
        }

        private sealed class PublicationFailure : FileStream
        {
            private readonly long _headerPosition;
            internal PublicationFailure(string file, long headerPosition)
                : base(file, FileMode.Open, FileAccess.ReadWrite) => _headerPosition = headerPosition;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Position == _headerPosition && count == 8192) throw new IOException("interrupted conversion");
                base.Write(buffer, offset, count);
            }
        }

        private sealed class WalFailure : FileStream
        {
            private readonly long _failAt;
            internal WalFailure(string file, long failAt)
                : base(file, FileMode.Create, FileAccess.ReadWrite) => _failAt = failAt;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Position >= _failAt && count != 0) throw new IOException("interrupted conversion");
                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (Position >= _failAt && buffer.Length != 0) throw new IOException("interrupted conversion");
                base.Write(buffer);
            }
        }
    }
}
