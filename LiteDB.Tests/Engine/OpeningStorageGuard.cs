using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    internal sealed class OpeningStorageGuard : ICoordinationSignals
    {
        internal int Depth;
        internal int Begins;
        internal int Mutations;
        internal bool Armed = true;
        internal bool TearDataWrite;

        public void StructuralBegin() { Depth++; Begins++; }
        public void StructuralEnd(int version) { Depth--; Depth.Should().BeGreaterThanOrEqualTo(0); }
        public void SlotReused() { }
        public void Committed(int version) { }

        internal void Mutation()
        {
            if (!Armed) return;
            Depth.Should().BeGreaterThan(0, "opening must exclude new cached admissions before modifying existing storage");
            Mutations++;
        }

        internal FileStream Open(string filename, bool data) => new GuardedFile(filename, this, data);

        private sealed class GuardedFile : FileStream
        {
            private readonly OpeningStorageGuard _guard;
            private readonly bool _data;

            internal GuardedFile(string filename, OpeningStorageGuard guard, bool data)
                : base(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)
            { _guard = guard; _data = data; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_data) _guard.Mutation();
                if (_data && _guard.Armed && _guard.TearDataWrite)
                {
                    _guard.TearDataWrite = false;
                    base.Write(buffer, offset, Math.Min(7, count));
                    base.Flush(true);
                    throw new IOException("injected opening write tear");
                }
                base.Write(buffer, offset, count);
            }

            public override void SetLength(long value)
            {
                // Growing/alignment-padding the append-only WAL does not destroy
                // observable bytes. Truncation and every data resize do.
                if (_data || value < Length) _guard.Mutation();
                base.SetLength(value);
            }
        }
    }
}
