using System;
using System.IO;

namespace LiteDB.Engine
{
    /// <summary>
    /// A caller stream other than a MemoryStream may still hold a write after it returned (a
    /// BufferedStream, a FileStream with a large buffer) and write it on at its next access, on
    /// whichever thread makes it. The engine's wrappers of one caller stream share this record, read
    /// and changed only under the lock on that stream: which wrapper's write the stream may still
    /// hold, and a failure another access met while writing it on.
    /// A wrapper's own accesses need nothing: a failure there is its own. Any other access (a
    /// reader's seek) first flushes the stream. That flush only writes (a sync is the writer's), so
    /// any failure there, also one that reads like "cannot sync", may have torn the held write: it
    /// fails that access as a write failure of the file (the engine continues read-only, decision 6)
    /// and is handed to the writer, whose next write, flush or truncation throws it, so the writer
    /// never acknowledges what the stream tore (a WAL batch stops the engine). After a failure the
    /// writer heard itself, nobody writes on for it. Nothing is flushed per
    /// write: a caller stream whose Flush() syncs is synced once per WAL batch or sync, as in 5.0.21.
    /// </summary>
    internal sealed class HeldWrites
    {
        private readonly FileOrigin _origin;
        private object _writer;
        private object _tornWriter;
        private Exception _writeOnFailure;

        internal HeldWrites(FileOrigin origin) => _origin = origin;

        /// <summary><paramref name="writer"/> writes: the stream may hold it until the next flush.</summary>
        internal void Wrote(object writer) => _writer = writer;

        /// <summary>The stream was flushed: it holds nothing.</summary>
        internal void Flushed() => _writer = null;

        /// <summary>
        /// A write, flush or truncation failed and its caller heard it: nobody writes on for the
        /// writer after its failure (decision 6: nothing more is written or synced through the handle).
        /// </summary>
        internal void Heard() => _writer = null;

        /// <summary>
        /// Before <paramref name="accessor"/> uses the stream: flush a write it holds for another
        /// wrapper, and hand a failure there to that writer.
        /// </summary>
        internal void WriteOn(Stream stream, object accessor)
        {
            if (_writer == null || ReferenceEquals(_writer, accessor)) return;
            try
            {
                stream.Flush();
            }
            catch (Exception ex)
            {
                if (_writeOnFailure == null)
                {
                    _tornWriter = _writer;
                    _writeOnFailure = ex;
                }
                // A write failure of this file, also on a reader's call.
                WriteFailure.InFile(ex, _origin);
                throw;
            }
            _writer = null;
        }

        /// <summary>Before <paramref name="writer"/> writes, flushes or truncates: throw a failure that tore its held write.</summary>
        internal void ThrowIfTorn(object writer)
        {
            if (_writeOnFailure == null || !ReferenceEquals(_tornWriter, writer)) return;
            var failure = _writeOnFailure;
            _writeOnFailure = null;
            _tornWriter = null;
            throw WriteFailure.InFile(new IOException("A write the stream still held failed when another access wrote it on: " +
                failure.Message, failure), _origin);
        }
    }
}
