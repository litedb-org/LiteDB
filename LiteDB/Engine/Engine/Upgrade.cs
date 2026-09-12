using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            try
            {
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
            }
            finally
            {
                _bufferPool.Return(buffer, true);
            }
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
    }
}
