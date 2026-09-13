using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// FileStream disk implementation of disk factory
    /// [ThreadSafe]
    /// </summary>
    internal class FileStreamFactory : IStreamFactory
    {
        private readonly string _filename;
        private readonly string _password;
        private readonly bool _readonly;
        private readonly bool _hidden;
        private readonly bool _useAesStream;

        public FileStreamFactory(string filename, string password, bool readOnly, bool hidden, bool useAesStream = true)
        {
            _filename = filename;
            _password = password;
            _readonly = readOnly;
            _hidden = hidden;
            _useAesStream = useAesStream;
        }

        /// <summary>
        /// Get data filename
        /// </summary>
        public string Name => Path.GetFileName(_filename);

        /// <summary>
        /// Create new data file FileStream instance based on filename
        /// </summary>
        public Stream GetStream(bool canWrite, bool sequencial)
        {
            var write = canWrite && (_readonly == false);

            var fileMode = _readonly ? System.IO.FileMode.Open : System.IO.FileMode.OpenOrCreate;
            var fileAccess = write ? FileAccess.ReadWrite : FileAccess.Read;
            var fileShare = write ? FileShare.Read : FileShare.ReadWrite;
            var fileOptions = sequencial ? FileOptions.SequentialScan : FileOptions.RandomAccess;

            var isNewFile = write && this.Exists() == false;

            var stream = new FileStream(_filename,
                fileMode,
                fileAccess,
                fileShare,
                PAGE_SIZE,
                fileOptions);

            if (isNewFile && _hidden)
            {
                File.SetAttributes(_filename, FileAttributes.Hidden);
            }

            return _password == null || !_useAesStream ? (Stream)stream : new AesStream(_password, stream);
        }

        /// <summary>
        /// Get file length using FileInfo. Crop file length if not length % PAGE_SIZE
        /// </summary>
        public long GetLength()
        {
            // if not file do not exists, returns 0
            if (!this.Exists()) return 0;

            // get physical file length from OS
            var length = new FileInfo(_filename).Length;

            // if file length are not PAGE_SIZE module, maybe last save are not completed saved on disk
            // crop file removing last uncompleted page saved
            if (length % PAGE_SIZE != 0)
            {
                length = length - (length % PAGE_SIZE);

                using (var fs = new FileStream(
                    _filename,
                    System.IO.FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    PAGE_SIZE,
                    FileOptions.SequentialScan))
                {
                    fs.SetLength(length);
                    fs.FlushToDisk();
                }
            }

            // if encrypted must remove salt first page (only if page contains data)
            return length > 0 ?
                length - (_password == null ? 0 : PAGE_SIZE) :
                0;
        }

        /// <summary>
        /// Check if file exists (without open it)
        /// </summary>
        public bool Exists()
        {
            return File.Exists(_filename);
        }

        /// <summary>
        /// Delete file (must all stream be closed)
        /// </summary>
        public void Delete()
        {
            File.Delete(_filename);
        }

        /// <summary>
        /// Test if this file are locked by another process
        /// </summary>
        public bool IsLocked() => this.Exists() && FileHelper.IsFileLocked(_filename);

        /// <summary>
        /// Close all stream on end
        /// </summary>
        public bool CloseOnDispose => true;
    }
}