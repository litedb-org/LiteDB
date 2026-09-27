using System;
using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal static class WalRetirementReader
    {
        /// <summary>
        /// Replay original witnesses at their original physical positions, followed
        /// by any new incarnation. A torn unconfirmed reuse cannot invalidate older
        /// acknowledged transactions; its later confirmation still needs a matching
        /// count and digest. Root records themselves are never reusable.
        /// </summary>
        internal static IEnumerable<PageBuffer> Read(Stream raw, WalChecksum checksum, long length,
            Action<byte[]> observe = null, long from = 0)
        {
            var bytes = new byte[WalChecksum.FrameSize];
            for (long position = from; position < length; position += PAGE_SIZE)
            {
                checksum.Retirement.Slots.TryGetValue(position, out var witnesses);
                if (witnesses != null)
                    foreach (var witness in witnesses) yield return witness.Replay();
                raw.Position = position / PAGE_SIZE * WalChecksum.FrameSize;
                var count = raw.ReadFully(bytes, 0, bytes.Length);
                if (count != bytes.Length) throw new PageChecksumException(FileOrigin.Log, position);
                observe?.Invoke(bytes);
                var page = new PageBuffer(bytes, 0, 0) { Position = position, Origin = FileOrigin.Log };
                var valid = true;
                try { page.WalFrame = checksum.Validate(page, new BufferSlice(bytes, PAGE_SIZE, WalChecksum.MetadataSize), position); }
                catch (PageChecksumException) when (witnesses != null) { valid = false; }
                if (!valid) continue;
                if (page.WalFrame.RetirementRecord) continue;
                var retired = false;
                if (witnesses != null)
                    foreach (var witness in witnesses) retired |= witness.Matches(page);
                if (!retired) yield return page;
            }
        }
    }
}
