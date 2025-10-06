using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// [ThreadSafe]
    /// </summary>
    internal class RebuildService
    {
        private readonly EngineSettings _settings;
        private readonly int _fileVersion;

        public RebuildService(EngineSettings settings)
        {
            _settings = settings;

            // test for prior version
            var bufferV7 = this.ReadFirstBytes(false);
            if (FileReaderV7.IsVersion(bufferV7))
            {
                _fileVersion = 7;
                return;
            }

            // open, read first 16kb, and close data file
            var buffer = this.ReadFirstBytes();

            // test for valid reader to use
            _fileVersion = FileReaderV8.IsVersion(buffer) ? 8 : throw LiteException.InvalidDatabase();
        }

        private static uint GetSourceMaxItemsCount(EngineSettings settings)
        {
            long dataBytes = new FileInfo(settings.Filename).Length;

            var logFile = FileHelper.GetLogFile(settings.Filename);
            long logBytes = File.Exists(logFile) ? new FileInfo(logFile).Length : 0;
            // ((pages in data+log) + 10) * 255
            return (uint)((((dataBytes + logBytes) / PAGE_SIZE) + 10) * byte.MaxValue);
        }

        public long Rebuild(RebuildOptions options)
        {
            var backupFilename = FileHelper.GetSuffixFile(_settings.Filename, "-backup", true);
            var backupLogFilename = FileHelper.GetSuffixFile(FileHelper.GetLogFile(_settings.Filename), "-backup", true);
            var tempFilename = FileHelper.GetSuffixFile(_settings.Filename, "-temp", true);

            // open file reader
            using (var reader = _fileVersion == 7 ?
                new FileReaderV7(_settings) :
                (IFileReader)new FileReaderV8(_settings, options.Errors))
            {
                // open file reader and ready to import to new temp engine instance
                reader.Open();

                // open new engine to recive all data readed from FileReader
                using (var engine = new LiteEngine(new EngineSettings
                {
                    Filename = tempFilename,
                    Collation = options.Collation,
                    Password = options.Password,
                }))
                {
                    // copy all database to new Log file with NO checkpoint during all rebuild
                    engine.Pragma(Pragmas.CHECKPOINT, 0);

                    // compute the correct MAX_ITEMS_COUNT from the *source* file
                    uint maxItemsCount = GetSourceMaxItemsCount(_settings);

                    // rebuild all content from reader into new engine
                    engine.RebuildContent(reader, maxItemsCount);

                    // insert error report
                    if (options.IncludeErrorReport && options.Errors.Count > 0)
                    {
                        var report = options.GetErrorReport();

                        engine.Insert("_rebuild_errors", report, BsonAutoId.Int32);
                    }

                    // update pragmas
                    var pragmas = reader.GetPragmas();

                    engine.Pragma(Pragmas.CHECKPOINT, pragmas[Pragmas.CHECKPOINT]);
                    engine.Pragma(Pragmas.TIMEOUT, pragmas[Pragmas.TIMEOUT]);
                    engine.Pragma(Pragmas.LIMIT_SIZE, pragmas[Pragmas.LIMIT_SIZE]);
                    engine.Pragma(Pragmas.UTC_DATE, pragmas[Pragmas.UTC_DATE]);
                    engine.Pragma(Pragmas.USER_VERSION, pragmas[Pragmas.USER_VERSION]);

                    // after rebuild, copy log bytes into data file
                    engine.Checkpoint();
                }
            }

            // if log file exists, rename as backup file
            var logFile = FileHelper.GetLogFile(_settings.Filename);

            if (File.Exists(logFile))
            {
                File.Move(logFile, backupLogFilename);
            }

            // rename source filename to backup name
            FileHelper.Exec(5, () =>
            {
                File.Move(_settings.Filename, backupFilename);
            });

            // rename temp file into filename
            File.Move(tempFilename, _settings.Filename);


            // get difference size
            return
                new FileInfo(backupFilename).Length -
                new FileInfo(_settings.Filename).Length;
        }

        /// <summary>
        /// Read first 16kb (2 PAGES) in bytes
        /// </summary>
        private byte[] ReadFirstBytes(bool useAesStream = true)
        {
            var buffer = new byte[PAGE_SIZE * 2];
            var factory = _settings.CreateDataFactory(useAesStream);

            using (var stream = factory.GetStream(false, true))
            {
                stream.Position = 0;
                stream.Read(buffer, 0, buffer.Length);
            }

            return buffer;
        }
    }
}