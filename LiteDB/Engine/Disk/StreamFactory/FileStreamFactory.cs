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
        private readonly Action<string> _setHiddenAttribute;
#if DEBUG || TESTING
        internal Action BeforeReadLength;
#endif

        public FileStreamFactory(
            string filename,
            string password,
            bool readOnly,
            bool hidden,
            bool useAesStream = true,
            Action<string> setHiddenAttribute = null)
        {
            _filename = filename;
            _password = password;
            _readonly = readOnly;
            _hidden = hidden;
            _useAesStream = useAesStream;
            _setHiddenAttribute = setHiddenAttribute ?? (value => File.SetAttributes(value, FileAttributes.Hidden));
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

            FileStream stream;
            try
            {
                stream = new FileStream(_filename,
                    fileMode,
                    fileAccess,
                    fileShare,
                    PAGE_SIZE,
                    fileOptions);
            }
            catch (IOException ex) when (_readonly && !canWrite &&
                (ex is FileNotFoundException || ex is DirectoryNotFoundException))
            {
                throw new LiteException(LiteException.FILE_NOT_FOUND, ex,
                    "File '{0}' does not exist and cannot be created in read-only mode.", _filename);
            }

            if (isNewFile && _hidden)
            {
                try
                {
                    _setHiddenAttribute(_filename);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            return _password == null || !_useAesStream ? (Stream)stream : new AesStream(_password, stream);
        }

        /// <summary>
        /// Get the logical file length without modifying the file.
        /// </summary>
        public long GetLength()
        {
            // if not file do not exists, returns 0
            if (!this.Exists()) return 0;

            long length;
            try
            {
#if DEBUG || TESTING
                BeforeReadLength?.Invoke();
#endif
                length = new FileInfo(_filename).Length;
            }
            catch (IOException ex) when (_readonly &&
                (ex is FileNotFoundException || ex is DirectoryNotFoundException))
            {
                throw new LiteException(LiteException.FILE_NOT_FOUND, ex,
                    "File '{0}' does not exist and cannot be created in read-only mode.", _filename);
            }

            if (_password == null || length == 0)
            {
                return length;
            }

            // A partial encrypted preamble is treated as an interrupted creation.
            // Any other short, non-empty input must still reach validation.
            if (length < PAGE_SIZE)
            {
                using (var stream = new FileStream(
                    _filename,
                    System.IO.FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    1,
                    FileOptions.SequentialScan))
                {
                    return stream.ReadByte() == 1 ? 0 : length;
                }
            }

            // Encrypted files reserve the first physical page for their salt.
            return length - PAGE_SIZE;
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

        public void TrimCapacity(Stream stream)
        {
        }

        public void Dispose()
        {
        }
    }
}
