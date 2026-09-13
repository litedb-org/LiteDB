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
        /// Upgrade old version of LiteDB into new LiteDB file structure. Returns true if database was completed converted
        /// If database already in current version just return false
        /// </summary>
        public static bool Upgrade(Stream stream, string password = null, Collation collation = null)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                var settings = new EngineSettings
                {
                    DataStream = ms,
                    Password = password,
                    Collation = collation
                };



                if (!TryUpgradeStreamInternal(password, stream, settings))
                    return false;
                ms.Flush();
                ms.Seek(0, SeekOrigin.Begin);

                stream.Seek(0, SeekOrigin.Begin);
                stream.SetLength(0);
                ms.CopyTo(stream);

                stream.Flush();
                stream.Seek(0, SeekOrigin.Begin);
                return true;
            }

        }

        private static bool TryUpgradeStreamInternal(string password, Stream stream, EngineSettings settings)
        {
            var buffer = new byte[PAGE_SIZE * 2];
            IFileReader reader;
            // read first 16k
            stream.Read(buffer, 0, buffer.Length);

            // checks if v8 plain data or encrypted (first byte = 1)
            if ((Encoding.UTF8.GetString(buffer, HeaderPage.P_HEADER_INFO, HeaderPage.HEADER_INFO.Length) == HeaderPage.HEADER_INFO &&
                 buffer[HeaderPage.P_FILE_VERSION] == HeaderPage.FILE_VERSION) ||
                buffer[0] == 1)
            {
                return false;
            }

            // checks if v7 (plain or encrypted)
            if (Encoding.UTF8.GetString(buffer, 25, HeaderPage.HEADER_INFO.Length) == HeaderPage.HEADER_INFO &&
                buffer[52] == 7)
            {
                reader = new FileReaderV7(new EngineSettings { DataStream = stream, Password = password });
                reader.Open();
            }
            else
            {
                throw new LiteException(0, "Invalid data file format to upgrade");
            }

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

            return true;
        }
    }
}
