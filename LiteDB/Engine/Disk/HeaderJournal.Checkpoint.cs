using System;
using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed partial class HeaderJournal
    {
        internal static uint ComputeVerifiedBody(Stream stream, long length, byte[] header, WalChecksum expected,
            ISet<uint> expectedTransactions = null)
        {
            if (WalPadding.TrailingBytes(length) != 0) throw new PageChecksumException(FileOrigin.Log, length);
            var salt = new byte[16];
            Buffer.BlockCopy(header, WalChecksum.SaltPosition, salt, 0, salt.Length);
            var checksums = new WalChecksum();
            checksums.Reset(salt);
            var crc = uint.MaxValue;

            checksums.Retirement = WalRetirement.Load(new BufferSlice(header, 0, PAGE_SIZE), stream, checksums);
            // Hash the same physical bytes from which the logical witnesses and
            // surviving payloads are verified, including abandoned reused slots.
            var frames = WalRetirementReader.Read(stream, checksums, length / WalChecksum.FrameSize * PAGE_SIZE,
                bytes => crc = Crc32C.Update(crc, bytes, 0, bytes.Length));

            var recovery = new WalRecovery();
            var confirmed = expectedTransactions == null ? null : new HashSet<uint>();
            foreach (var page in recovery.Read(frames))
            {
                if (confirmed != null && page.ReadBool(BasePage.P_IS_CONFIRMED) &&
                    !confirmed.Add(page.ReadUInt32(BasePage.P_TRANSACTION_ID)))
                    throw new PageChecksumException(FileOrigin.Log, page.Position);
            }
            recovery.RequireRetirement(checksums.Retirement);
            if (recovery.InvalidTail || recovery.Sequence != expected.Sequence ||
                recovery.ConfirmedEnd != expected.LastConfirmedPosition + PAGE_SIZE ||
                (confirmed != null && !confirmed.SetEquals(expectedTransactions)))
                throw new PageChecksumException(FileOrigin.Log, recovery.ConfirmedEnd);
            var padding = new byte[length % WalChecksum.FrameSize];
            stream.Position = length - padding.Length;
            stream.ReadRequired(padding, 0, padding.Length);
            crc = Crc32C.Update(crc, padding, 0, padding.Length);
            return ~crc;
        }
    }
}
