using System;
using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal partial class WalIndexService
    {
        internal SharedWriterResume CaptureWriterResume(HeaderPage header)
        {
            if (_snapshots.Count != 0 || _confirmTransactions.Count > SharedWriterResume.MaximumEntries ||
                _confirmationPositions.Count > SharedWriterResume.MaximumEntries) return null;
            var entries = 0;
            foreach (var versions in _index.Values)
            {
                if (versions.Count > SharedWriterResume.MaximumEntries - entries) return null;
                entries += versions.Count;
            }
            var saved = _disk.CaptureWriterResume(header, _currentReadVersion);
            if (saved == null) return null;
            // Copy live entries, not retained backing capacities from a formerly large
            // engine. Clear/pruning can leave much larger arrays than Count suggests.
            saved.Index = new Dictionary<uint, List<KeyValuePair<int, long>>>(_index.Count);
            foreach (var entry in _index)
                saved.Index.Add(entry.Key, new List<KeyValuePair<int, long>>(entry.Value));
            saved.ConfirmedTransactions = new HashSet<uint>();
            foreach (var transaction in _confirmTransactions) saved.ConfirmedTransactions.Add(transaction);
            saved.ConfirmationPositions = new Dictionary<int, long>(_confirmationPositions);
            saved.ReadVersion = _currentReadVersion;
            saved.LastTransactionID = _lastTransactionID;
            saved.BackfillVersion = _backfillVersion;
            return saved;
        }

        internal bool TryResumeWriter(SharedWriterResume saved, Func<bool> fenceValid,
            ref HeaderPage header, Action<HeaderPage> validateHeader)
        {
            if (saved == null || fenceValid == null || !fenceValid() ||
                !_disk.ValidateWriterResume(saved, header) || !fenceValid()) return false;
            _index = saved.Index;
            _confirmTransactions = saved.ConfirmedTransactions;
            _confirmationPositions = saved.ConfirmationPositions;
            _currentReadVersion = saved.ReadVersion;
            _lastTransactionID = saved.LastTransactionID;
            _backfillVersion = saved.BackfillVersion;
            _disk.ImportWriterResume(saved);
            var buffer = new PageBuffer(saved.LogicalHeader, 0, 0);
            CopyConfirmedHeader(ref header, buffer);
            // No transaction was live at capture; only validated retirement records
            // may follow the last confirmation in the unchanged prefix.
            // Fresh recovery validates every appended transaction and handles torn tails
            // using exactly the same logic as a full open, including all observed IDs.
            RestoreIndex(ref header, new WalRecovery(saved.Sequence, saved.End),
                _disk.ReadLogFrom(saved.End), validateHeader);
            return true;
        }
    }
}
