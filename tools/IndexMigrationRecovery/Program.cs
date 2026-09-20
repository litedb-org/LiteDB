using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Engine;

internal static class Program
{
    private const int Count = 2000;

    private static int Main(string[] args)
    {
        var filename = args[1];
        var password = args[2] == "plain" ? null : "migration-recovery";
        if (args[0] == "create") Create(filename, password);
        else if (args[0] == "verify") Verify(filename, password);
        else
        {
            var controller = new FaultController(args[3], args[4]);
            using var data = new FaultStream(filename, password, false, controller);
            using var log = new FaultStream(Path.ChangeExtension(filename, null) + "-log.db", password, true, controller);
            try
            {
                using var engine = new LiteEngine(new EngineSettings
                {
                    DataStream = data, LogStream = log, TransactionPageLimit = 8
                });
                engine.Checkpoint();
            }
            catch (IOException ex) when (ex.Message == "Injected migration write failure")
            {
                return 17;
            }
            throw new Exception("Requested fault was not reached");
        }
        return 0;
    }

    private static void Create(string filename, string password)
    {
        using (var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password }))
        {
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, Count).Select(i => new BsonDocument
            {
                ["_id"] = i, ["key"] = new string('A', 200) + i, ["values"] = new BsonArray { i, -i }
            }));
            rows.EnsureIndex("computed", "LOWER($.key)", true);
            rows.EnsureIndex("values", "$.values[*]");
            db.Checkpoint();
        }
        // Real files with legacy header metadata exercise the migration state
        // machine; the separate compatibility harness uses actual 5.0.21 files.
        using var file = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite);
        using var stream = password == null ? (Stream)file : new AesStream(password, file);
        var header = new byte[8192];
        stream.Position = 0;
        stream.ReadExactly(header);
        header[59] = 8;
        Array.Clear(header, 92, 4);
        header[109] = 0;
        stream.Position = 0;
        stream.Write(header, 0, header.Length);
        stream.Flush();
        file.Flush(true);
    }

    private static void Verify(string filename, string password)
    {
        using (var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password }))
        {
            var rows = db.GetCollection("rows");
            if (rows.Count() != Count) throw new Exception("Lost documents");
            foreach (var i in Enumerable.Range(1, Count))
            {
                if (rows.Count("LOWER($.key) = @0", new string('a', 200) + i) != 1 ||
                    rows.Count("$.values ANY = @0", -i) != 1)
                    throw new Exception("Lost computed/multikey entry " + i);
            }
            if (rows.Query().OrderBy("LOWER($.key)").ToArray().Length != Count)
                throw new Exception("Index traversal lost documents");
            var document = rows.FindById(2);
            document["key"] = "changed";
            rows.Update(document);
            if (rows.Count("LOWER($.key) = 'changed'") != 1) throw new Exception("Update failed");
            if (!rows.Delete(2) || rows.Count("$.values ANY = -2") != 0) throw new Exception("Delete failed");
            db.Checkpoint();
        }
        using var reopened = new LiteDatabase(new ConnectionString { Filename = filename, Password = password, ReadOnly = true });
        if (reopened.GetCollection("rows").Count() != Count - 1) throw new Exception("Checkpoint lost changes");
    }

    internal sealed class FaultController
    {
        private readonly string _mode;
        private readonly string _stage;
        private int _walPages;
        private bool _committed;
        private bool _triggered;

        internal FaultController(string mode, string stage) { _mode = mode; _stage = stage; }

        internal bool ShouldFail(bool log, long position, byte[] buffer, int offset, int count)
        {
            if (_triggered || count != 8192) return false;
            var commit = log && BitConverter.ToUInt32(buffer, offset) == 0 &&
                buffer[offset + 18] != 0 && buffer[offset + 109] == 1;
            var hit = (_stage == "promotion" && !log && position == 0 && buffer[offset + 59] == 10) ||
                (_stage == "wal" && log && ++_walPages == 3) ||
                (_stage == "commit" && commit) || (_stage == "checkpoint" && !log && _committed);
            _committed |= commit;
            _triggered = hit;
            return hit;
        }

        internal bool Partial => _stage == "wal" || _stage == "checkpoint";

        internal void Fail()
        {
            Console.WriteLine("FAULT:" + _mode + ":" + _stage);
            Console.Out.Flush();
            if (_mode == "crash")
            {
                Process.GetCurrentProcess().Kill();
                System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
            }
            throw new IOException("Injected migration write failure");
        }
    }

    private sealed class FaultStream : Stream
    {
        private readonly FaultFileStream _file;
        private readonly Stream _inner;
        private readonly bool _log;
        private readonly FaultController _controller;

        internal FaultStream(string filename, string password, bool log, FaultController controller)
        {
            _file = new FaultFileStream(filename, controller);
            _inner = password == null ? (Stream)_file : new AesStream(password, _file);
            _log = log;
            _controller = controller;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var fail = _controller.ShouldFail(_log, Position, buffer, offset, count);
            _file.Partial = fail && _controller.Partial;
            _inner.Write(buffer, offset, count);
            if (fail)
            {
                Flush();
                _controller.Fail();
            }
        }

        public override void Flush() { _inner.Flush(); _file.Flush(true); }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FaultFileStream : FileStream
    {
        private readonly FaultController _controller;
        internal bool Partial;

        internal FaultFileStream(string filename, FaultController controller)
            : base(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)
        {
            _controller = controller;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!Partial) { base.Write(buffer, offset, count); return; }
            Partial = false;
            base.Write(buffer, offset, Math.Min(count, 128));
            Flush(true);
            _controller.Fail();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!Partial) { base.Write(buffer); return; }
            Partial = false;
            base.Write(buffer.Slice(0, Math.Min(buffer.Length, 128)));
            Flush(true);
            _controller.Fail();
        }
    }
}
