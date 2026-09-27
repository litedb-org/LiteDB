using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>Verifies frames and complete transactions before exposing confirmation.</summary>
    internal sealed class WalRecovery
    {
        /// <summary>Start fresh, or resume after an earlier scan that ended at this sequence and confirmed end.</summary>
        internal WalRecovery(long sequence = 0, long confirmedEnd = 0)
        {
            Sequence = sequence;
            ConfirmedEnd = confirmedEnd;
        }

        internal long ConfirmedEnd { get; private set; }
        internal long Sequence { get; private set; }
        internal bool InvalidTail { get; private set; }

        internal void RequireRetirement(WalRetirement retirement)
        {
            // A published retirement root can accompany partially checkpointed
            // data. Losing an older commit is never an ignorable recovery tail.
            if (Sequence < retirement.Sequence)
                throw new PageChecksumException(FileOrigin.Log, ConfirmedEnd);
            ConfirmedEnd = Math.Max(ConfirmedEnd, retirement.End);
        }

        internal IEnumerable<PageBuffer> Read(IEnumerable<PageBuffer> pages)
        {
            var transactions = new Dictionary<uint, WalChecksum.Summary>();
            using (var source = pages.GetEnumerator())
            {
                while (true)
                {
                    bool next;
                    try { next = source.MoveNext(); }
                    catch (PageChecksumException) { InvalidTail = true; yield break; }
                    if (!next) yield break;
                    var page = source.Current;
                    var id = page.ReadUInt32(BasePage.P_TRANSACTION_ID);
                    transactions.TryGetValue(id, out var summary);
                    summary.Count = checked(summary.Count + 1);
                    summary.Digest ^= page.WalFrame.Contribution;
                    if (page.ReadBool(BasePage.P_IS_CONFIRMED))
                    {
                        if (page.WalFrame.Count != summary.Count || page.WalFrame.Digest != summary.Digest ||
                            page.WalFrame.Sequence != Sequence + 1)
                        {
                            InvalidTail = true;
                            yield break;
                        }
                        Sequence++;
                        ConfirmedEnd = page.Position + PAGE_SIZE;
                        transactions.Remove(id);
                    }
                    else
                    {
                        if (page.WalFrame.Sequence != 0)
                        {
                            InvalidTail = true;
                            yield break;
                        }
                        transactions[id] = summary;
                    }
                    yield return page;
                }
            }
        }
    }
}
