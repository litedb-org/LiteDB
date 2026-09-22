using System;
using System.IO;
using LiteDB;
using LiteDB.Engine;

namespace VectorCompatibility.Current
{
    internal static class InterruptedConversion
    {
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
    }
}
