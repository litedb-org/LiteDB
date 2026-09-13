using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Simple Stream disk implementation of disk factory - used for Memory/Temp database
    /// [ThreadSafe]
    /// </summary>
    internal class StreamFactory : IStreamFactory
    {
        private readonly Stream _stream;
        private readonly string _password;

        public StreamFactory(Stream stream, string password)
        {
            _stream = stream;
            _password = password;
        }

        /// <summary>
        /// Stream has no name (use stream type)
        /// </summary>
        public string Name => _stream is MemoryStream ? ":memory:" : _stream is TempStream ? ":temp:" : ":stream:";

        /// <summary>
        /// Use ConcurrentStream wrapper to support multi thread in same Stream (using lock control)
        /// </summary>
        public Stream GetStream(bool canWrite, bool sequencial)
        {
            if (_password == null)
            {
                return new ConcurrentStream(_stream, canWrite);
            }
            else
            {
                return new AesStream(_password, new ConcurrentStream(_stream, canWrite));
            }
        }

        /// <summary>
        /// Get file length using _stream.Length
        /// </summary>
        public long GetLength()
        {
            var length = _stream.Length;

            // if file length are not PAGE_SIZE module, maybe last save are not completed saved on disk
            // crop file removing last uncompleted page saved
            if (length % PAGE_SIZE != 0)
            {
                length = length - (length % PAGE_SIZE);

                _stream.SetLength(length);
                _stream.FlushToDisk();
            }

            return length > 0 ?
                length - (_password == null ? 0 : PAGE_SIZE) :
                0;
        }

        /// <summary>
        /// Check if file exists based on stream length
        /// </summary>
        public bool Exists() => _stream.Length > 0;

        /// <summary>
        /// There is no delete method in Stream factory
        /// </summary>
        public void Delete()
        {
        }

        /// <summary>
        /// Test if this file are locked by another process (there is no way to test when Stream only)
        /// </summary>
        public bool IsLocked() => false;

        /// <summary>
        /// Do no dispose on finish
        /// </summary>
        public bool CloseOnDispose => false;
    }
}