using System.Linq;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>Lifetime-chaos judging: permitted outcomes per node and the acknowledged effects it leaves in the file models.</summary>
    internal sealed partial class LifetimeChaosExecution
    {
        /// <summary>
        /// What may disturb <paramref name="node"/>: Dispose of its connection (maintenance or a callback
        /// body) permits disposed/refused failures; a rebuild of its file permits disposed failures and
        /// timed-out waits; a fatal stop of its file permits the fault and disposed failures. In Direct
        /// mode an operation waiting for exclusive access (Checkpoint, Rebuild) gives up after TIMEOUT,
        /// and the operations it fences may give up too.
        /// </summary>
        private Permit Permitted(ChaosNode node, int file, int slot)
        {
            var permit = Permit.Success;
            var target = _program.Nodes[_program.MaintenanceNode];
            var closed = C.Maintenance == ExplorerMaintenance.Close && target.File == file && target.Slot == slot ||
                _program.Nodes.Any(other => other.Body == ChaosBody.Dispose && other.File == file && other.Slot == slot);
            if (closed) permit |= Permit.Disposed | Permit.Refusal;
            if (C.Maintenance == ExplorerMaintenance.Rebuild && target.File == file) permit |= Permit.Disposed | Permit.Refusal | Permit.LockTimeout;
            if (C.Maintenance == ExplorerMaintenance.Fatal && target.File == file) permit |= Permit.Fatal | Permit.Disposed | Permit.Refusal;
            if (!C.Shared && _program.Nodes.Any(other => other.Op == ChaosOp.Checkpoint && other.File == file)) permit |= Permit.LockTimeout;
            return permit;
        }

        private void Judge(ChaosNode node)
        {
            var work = _works[node.Index];
            if (work == null) return;
            var permit = this.Permitted(node, node.File, node.Slot);
            _run.Judge(work, permit);
            var record = _records[node.Index];
            if (record.Ran && node.Body != ChaosBody.Await && node.Body != ChaosBody.None)
            {
                var nested = node.Body == ChaosBody.Peer ? this.Permitted(node, node.File, PeerSlot)
                    : node.Body == ChaosBody.OtherFile ? this.Permitted(node, OtherFile, 0) : permit;
                _run.Schedule.Event("outcome N" + node.Index + " callback " + ExplorerJudge.Describe(record.Error));
                ExplorerJudge.Judge("N" + node.Index + " callback", "Callback", record.Error, nested | Permit.Refusal | Permit.LockTimeout, record.Refused);
            }
            this.Account(node, work);
        }

        private void Account(ChaosNode node, ExplorerSchedule.Work work)
        {
            var model = _run.FileModel(node.File);
            var transactional = node.Op != ChaosOp.Checkpoint && node.Op != ChaosOp.Read && _run.Access.Transactional;
            var acknowledged = work.Ok && (!transactional || _committed[node.Index]);
            var uncertain = !work.Ok && (!transactional || _reachedCommit[node.Index]);
            void Row(int id, int value)
            {
                if (acknowledged) model.Acknowledge(node.Collection, id, value);
                else if (uncertain) model.Uncertain(node.Collection, id, value);
            }
            switch (node.Op)
            {
                case ChaosOp.InsertInput:
                    Row(2, 20 + node.Index);
                    Row(3, 30 + node.Index);
                    break;
                case ChaosOp.Write:
                    Row(1, 100 + node.Index);
                    break;
                case ChaosOp.Upload:
                    if (acknowledged) model.AcknowledgeFile("$/chaos/" + node.Collection, Content);
                    else if (uncertain || !work.Ok) model.UncertainFile("$/chaos/" + node.Collection, Content);
                    break;
            }
            var record = _records[node.Index];
            if (!record.Ran || record.Collection == null) return;
            var target = record.OtherFile ? _run.FileModel(OtherFile) : model;
            if (record.Error == null && (record.Independent || acknowledged)) target.Acknowledge(record.Collection, 5, 50);
            else target.Uncertain(record.Collection, 5, 50);
        }
    }
}
