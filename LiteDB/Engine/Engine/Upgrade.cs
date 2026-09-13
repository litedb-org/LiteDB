using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {

        private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

        /// <summary>
        /// If Upgrade=true, run this before open Disk service
        /// </summary>
        private void TryUpgrade()
        {
            var filename = _settings.Filename;

            // if file not exists, just exit
            if (!File.Exists(filename)) return;

            const int bufferSize = 1024;
            var buffer = _bufferPool.Rent(bufferSize);

            using (var stream = new FileStream(
                _settings.Filename,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read, bufferSize))
            {


                stream.Position = 0;
                stream.Read(buffer, 0, bufferSize);

                if (FileReaderV7.IsVersion(buffer) == false) return;
            }
            _bufferPool.Return(buffer, true);
            // run rebuild process
            this.Recovery(_settings.Collation);
        }

        /// <summary>
        /// Upgrade old version of LiteDB into new LiteDB file structure. Returns true if database was completed converted
        /// If database already in current version just return false
        /// </summary>
        [Obsolete("Upgrade your LiteDB v4 datafiles using Upgrade=true in EngineSettings. You can use upgrade=true in connection string.")]
        public static bool Upgrade(string filename, string password = null, Collation collation = null)
        {
            if (filename.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(filename));
            if (!File.Exists(filename)) return false;

            var settings = new EngineSettings
            {
                Filename = filename,
                Password = password,
                Collation = collation,
                Upgrade = true
            };

            using (var db = new LiteEngine(settings))
            {
                // database are now converted to v5
            }

            return true;
        }

        /// <summary>
        /// Upgrades a legacy LiteDB stream into a separate current-version stream.
        /// </summary>
        /// <param name="source">Legacy database stream. It must be readable and seekable.</param>
        /// <param name="destination">Destination for the converted database. It must be writable and seekable.</param>
        /// <param name="password">Password used by an encrypted legacy database.</param>
        /// <param name="collation">Collation for the converted database.</param>
        /// <returns>True when the source was converted; false when it is already in the current format.</returns>
        /// <remarks>
        /// Both streams remain open. The source is never modified and its original position is restored, including when
        /// conversion or destination writes fail. The destination remains unchanged when conversion is not required or
        /// fails before copying. After a successful conversion it is positioned at zero. If copying or flushing fails,
        /// the destination may contain an incomplete converted database and should be discarded.
        /// </remarks>
        public static bool Upgrade(Stream source, Stream destination, string password = null, Collation collation = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (ReferenceEquals(source, destination)) throw new ArgumentException("Source and destination streams must be different.", nameof(destination));
            if (!source.CanRead) throw new ArgumentException("Source stream must be readable.", nameof(source));
            if (!source.CanSeek) throw new ArgumentException("Source stream must be seekable.", nameof(source));
            if (!destination.CanWrite) throw new ArgumentException("Destination stream must be writable.", nameof(destination));
            if (!destination.CanSeek) throw new ArgumentException("Destination stream must be seekable.", nameof(destination));

            var sourcePosition = source.Position;

            try
            {
                source.Position = 0;

                using (var converted = new MemoryStream())
                {
                    var settings = new EngineSettings
                    {
                        DataStream = converted,
                        Password = password,
                        Collation = collation
                    };

                    if (!TryUpgradeStreamInternal(password, source, settings)) return false;

                    converted.Position = 0;
                    destination.Position = 0;
                    destination.SetLength(0);
                    converted.CopyTo(destination);
                    destination.Flush();
                    destination.Position = 0;

                    return true;
                }
            }
            finally
            {
                source.Position = sourcePosition;
            }
        }

        private static bool TryUpgradeStreamInternal(string password, Stream stream, EngineSettings settings)
        {
            var buffer = new byte[PAGE_SIZE * 2];
            var offset = 0;

            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer, offset, buffer.Length - offset);

                if (read == 0) break;

                offset += read;
            }

            // checks if v8 plain data or encrypted (first byte = 1)
            if ((Encoding.UTF8.GetString(buffer, HeaderPage.P_HEADER_INFO, HeaderPage.HEADER_INFO.Length) == HeaderPage.HEADER_INFO &&
                 buffer[HeaderPage.P_FILE_VERSION] == HeaderPage.FILE_VERSION) ||
                buffer[0] == 1)
            {
                return false;
            }

            if (!FileReaderV7.IsVersion(buffer))
            {
                throw new LiteException(0, "Invalid data file format to upgrade");
            }

            using (var reader = new FileReaderV7(
                new EngineSettings { Password = password },
                new NonDisposingStream(stream)))
            {
                reader.Open();

                using (var engine = new LiteEngine(settings))
                {
                    // copy all database to new Log file with NO checkpoint during all rebuild
                    engine.Pragma(Pragmas.CHECKPOINT, 0);

                    engine.RebuildContent(reader);

                    // after rebuild, copy log bytes into data file
                    engine.Checkpoint();

                    // re-enable auto-checkpoint pragma
                    engine.Pragma(Pragmas.CHECKPOINT, 1000);

                    // copy userVersion from old datafile
                    engine.Pragma("USER_VERSION", reader.GetPragmas()[Pragmas.USER_VERSION]);
                }
            }

            return true;
        }

        private sealed class NonDisposingStream : Stream
        {
            private readonly Stream _stream;

            public NonDisposingStream(Stream stream)
            {
                _stream = stream;
            }

            public override bool CanRead => _stream.CanRead;
            public override bool CanSeek => _stream.CanSeek;
            public override bool CanWrite => _stream.CanWrite;
            public override long Length => _stream.Length;
            public override long Position { get => _stream.Position; set => _stream.Position = value; }
            public override void Flush() => _stream.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
            public override void SetLength(long value) => _stream.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                // The caller owns the source stream.
            }
        }
    }
}
