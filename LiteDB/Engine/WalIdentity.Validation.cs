using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed partial class WalIdentity
    {
        internal enum Replay { Legacy, Current, Completed }

        internal static bool IsPrefix(PageBuffer page) =>
            page.ReadUInt32(BasePage.P_PAGE_ID) == 0 &&
            page.ReadByte(BasePage.P_PAGE_TYPE) == (byte)PageType.Header &&
            page.ReadUInt32(BasePage.P_TRANSACTION_ID) == 0 &&
            !page.ReadBool(BasePage.P_IS_CONFIRMED);

        internal static Replay Validate(PageBuffer dataHeader, Stream log)
        {
            var dataIdentity = Read(dataHeader);
            if (log.Length == 0) return Replay.Legacy;
            if (log.Length < PAGE_SIZE)
            {
                if (dataIdentity != null) throw Invalid("Incomplete WAL identity prefix.");
                return Replay.Legacy; // no complete legacy transaction can exist
            }
            var first = ReadHeader(log);
            if (IsPrefix(first))
            {
                var binding = Read(first);
                if (binding == null || dataIdentity == null || binding.Database != dataIdentity.Database)
                    throw Invalid("WAL belongs to a different database.");
                if (binding.Epoch == dataIdentity.Epoch) return Replay.Current;
                if (binding.Epoch == dataIdentity.Previous)
                {
                    if (!dataIdentity.MatchesCompletedWal(log))
                        throw Invalid("Completed WAL was changed after checkpoint; preserve it for manual recovery.");
                    return Replay.Completed;
                }
                throw Invalid("WAL belongs to a different checkpoint generation.");
            }
            if (HasHeaderIdentity(first)) throw Invalid("Damaged WAL identity prefix structure.");
            if (dataIdentity != null)
                throw Invalid("WAL has no identity prefix. Checkpoint it with the engine that created it before reopening.");

            // Old files lack generation metadata. Their confirmed header pages
            // still identify the original creation time, when one is present.
            log.Position = 0;
            while (log.Position + PAGE_SIZE <= log.Length)
            {
                log.ReadFully(first.Array, first.Offset, PAGE_SIZE);
                if (first.ReadUInt32(BasePage.P_PAGE_ID) != 0 ||
                    first.ReadByte(BasePage.P_PAGE_TYPE) != (byte)PageType.Header ||
                    !first.ReadBool(BasePage.P_IS_CONFIRMED)) continue;
                for (var i = 0; i < 8; i++)
                {
                    if (first[HeaderPage.P_CREATION_TIME + i] != dataHeader[HeaderPage.P_CREATION_TIME + i])
                        throw Invalid("Legacy WAL belongs to a different database.");
                }
                if (Read(first) != null) throw Invalid("WAL identity is missing from the data file.");
            }
            return Replay.Legacy;
        }
    }
}
