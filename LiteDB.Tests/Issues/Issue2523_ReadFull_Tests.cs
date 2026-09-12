using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2523_ReadFull_Tests
{
    [Fact]
    public void ReadFull_Must_See_Log_Page_Written_In_Same_Tick()
    {
        using var logStream = new DelayedPublishLogStream(); // <-- changed
        using var dataStream = new MemoryStream();

        var settings = new EngineSettings
        {
            DataStream = dataStream,
            LogStream = logStream
        };

        var state = new EngineState(null, settings);
        var disk = new DiskService(settings, state, new[] { 10 });

        try
        {
            // Arrange: create a single, full page
            var page = disk.NewPage();
            page.Fill(0xAC);

            // Act: write the page to the WAL/log
            disk.WriteLogDisk(new[] { page });

            // Assert: immediately read the log back fully.
            // Pre-fix: throws (ReadFull must read PAGE_SIZE bytes)
            // Post-fix: returns 1 page, filled with 0xAC
            var logPages = disk.ReadFull(FileOrigin.Log).ToList();

            logPages.Should().HaveCount(1);
            logPages[0].All(0xAC).Should().BeTrue();
        }
        finally
        {
            disk.Dispose();
        }
    }

    /// <summary>
    /// Stream that "accepts" writes (increases Length as the writer would see it),
    /// but hides the bytes from readers until Flush/FlushAsync publishes them.
    /// This mirrors the visibility gap the fix (stream.Flush()) closes.
    /// </summary>
    private sealed class DelayedPublishLogStream : Stream
    {
        private readonly MemoryStream _committed = new(); // bytes visible to readers
        private readonly List<(long Position, byte[] Data)> _pending = new();

        private long _writerLength;    // total bytes "written" by Write(...)
        private long _visibleLength;   // committed length visible to Read(...)
        private long _position;        // logical cursor for both read/write

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => true;

        // IMPORTANT: advertise writer's view of length (includes pending).
        public override long Length => _writerLength;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
            // Publish pending bytes
            foreach (var (pos, data) in _pending)
            {
                _committed.Position = pos;
                _committed.Write(data, 0, data.Length);
            }
            _pending.Clear();
            _committed.Flush();

            // Make everything visible
            _visibleLength = _writerLength;
        }

        public override System.Threading.Tasks.Task FlushAsync(System.Threading.CancellationToken cancellationToken)
        {
            Flush();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Serve only what has been published (visibleLength)
            if (_position >= _visibleLength) return 0;

            var available = (int)Math.Min(count, _visibleLength - _position);
            _committed.Position = _position;
            var read = _committed.Read(buffer, offset, available);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                // IMPORTANT: base End on the *advertised* writer length, not committed length
                SeekOrigin.End => _writerLength + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (_position < 0) throw new IOException("Negative position.");
            return _position;
        }

        public override void SetLength(long value)
        {
            if (value < 0) throw new IOException("Negative length.");
            // Adjust both writer length and (if shrinking) visible length.
            _writerLength = value;
            if (_visibleLength > value) _visibleLength = value;
            if (_committed.Length < value) _committed.SetLength(value);
            if (_position > value) _position = value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if ((uint)offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if ((uint)count > buffer.Length - offset) throw new ArgumentOutOfRangeException(nameof(count));

            // Capture write into pending (not visible yet)
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            _pending.Add((_position, copy));

            _position += count;
            if (_position > _writerLength) _writerLength = _position;
            // NOTE: _visibleLength is NOT updated here; only Flush() publishes writes.
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _committed.Dispose();
            base.Dispose(disposing);
        }
    }
}
