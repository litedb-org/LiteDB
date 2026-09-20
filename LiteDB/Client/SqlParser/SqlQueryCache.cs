using System;
using System.Collections.Generic;

namespace LiteDB
{
    internal sealed class SqlQueryCache
    {
        internal const int Capacity = 128;
        internal const int MaximumCommandLength = 8192;

        private readonly Dictionary<string, LinkedListNode<Entry>> _entries =
            new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _recent = new LinkedList<Entry>();

        internal bool TryGet(string command, out SqlQueryTemplate template, out bool repeated)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(command, out var node))
                {
                    _recent.Remove(node);
                    _recent.AddLast(node);
                    repeated = true;
                    template = node.Value.Template;
                    return template != null;
                }
            }
            template = null;
            repeated = false;
            return false;
        }

        internal void Add(string command, SqlQueryTemplate template)
        {
            if (command.Length > MaximumCommandLength) return;
            lock (_entries)
            {
                // The first use retains only the bounded command key. Construct an
                // unbound template only when the statement recurs while still here.
                if (_entries.TryGetValue(command, out var existing))
                {
                    if (existing.Value.Template == null) existing.Value.Template = template;
                    return;
                }
                if (_entries.Count == Capacity)
                {
                    _entries.Remove(_recent.First.Value.Command);
                    _recent.RemoveFirst();
                }
                var node = _recent.AddLast(new Entry { Command = command, Template = template });
                _entries.Add(command, node);
            }
        }

        private sealed class Entry
        {
            internal string Command;
            internal SqlQueryTemplate Template;
        }
    }
}
