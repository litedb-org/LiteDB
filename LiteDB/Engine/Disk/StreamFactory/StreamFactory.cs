using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
        private readonly bool _ownsStream;
        private int _disposed;

        public StreamFactory(Stream stream, string password, bool ownsStream = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _password = password;
            _ownsStream = ownsStream;
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
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(StreamFactory));

            // The factory owns the shared base stream; wrappers only own themselves.
            if (_password == null)
            {
                return new ConcurrentStream(_stream, canWrite, true);
            }
            else
            {
                return new AesStream(_password, new ConcurrentStream(_stream, canWrite, true), allowRecovery: false);
            }
        }

        /// <summary>
        /// Get the logical stream length without modifying the stream.
        /// </summary>
        public long GetLength()
        {
            lock (_stream)
            {
                var length = _stream.Length;

                if (_password == null || length == 0)
                {
                    return length;
                }

                // A partial encrypted preamble is treated as an interrupted creation.
                // Preserve the caller's position while checking its marker byte.
                if (length < PAGE_SIZE && _stream.CanRead && _stream.CanSeek)
                {
                    var position = _stream.Position;
                    try
                    {
                        _stream.Position = 0;
                        return _stream.ReadByte() == 1 ? 0 : length;
                    }
                    finally
                    {
                        _stream.Position = position;
                    }
                }

                // Encrypted streams reserve the first physical page for their salt.
                return length >= PAGE_SIZE ? length - PAGE_SIZE : length;
            }
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
        /// Wrappers are always disposed. Caller-owned base streams are protected
        /// by ConcurrentStream's leave-open mode.
        /// </summary>
        public bool CloseOnDispose => true;

        public void TrimCapacity(Stream stream)
        {
            if (!_ownsStream) return;

            // Capacity changes must use the same monitor as ConcurrentStream
            // readers, including when TempStream still stores data in memory.
            lock (_stream)
            {
                if (_stream is MemoryStream memory)
                {
                    memory.Capacity = checked((int)memory.Length);
                }
                else if (_stream is TempStream temp)
                {
                    temp.TrimCapacity();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (_ownsStream) _stream.Dispose();
        }
    }
}
