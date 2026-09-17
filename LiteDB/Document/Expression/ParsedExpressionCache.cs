using System;
using System.Collections.Generic;

namespace LiteDB
{
    internal sealed class ParsedExpressionCache
    {
        internal const int Capacity = 128;
        internal const int MaximumExpressionLength = 8192;

        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries =
            new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _recent = new LinkedList<Entry>();

        internal ParsedExpressionCache(int capacity = Capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        internal int Count
        {
            get { lock (_entries) return _entries.Count; }
        }

        internal bool TryGet(string source, out BsonExpression template, out bool repeated)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(source, out var node))
                {
                    _recent.Remove(node);
                    _recent.AddLast(node);
                    template = node.Value.Template;
                    repeated = true;
                    return template != null;
                }
            }
            template = null;
            repeated = false;
            return false;
        }

        internal void Add(string source, BsonExpression expression, bool repeated)
        {
            if (source.Length > MaximumExpressionLength) return;
            // First use retains only the text key. Copy recurring expressions before
            // publication, outside the lock, and never retain caller parameters.
            var template = repeated ? expression.WithoutParameters() : null;
            lock (_entries)
            {
                if (_entries.TryGetValue(source, out var existing))
                {
                    if (existing.Value.Template == null) existing.Value.Template = template;
                    return;
                }
                if (_entries.Count == _capacity)
                {
                    _entries.Remove(_recent.First.Value.Source);
                    _recent.RemoveFirst();
                }
                var node = _recent.AddLast(new Entry { Source = source, Template = template });
                _entries.Add(source, node);
            }
        }

        private sealed class Entry
        {
            internal string Source;
            internal BsonExpression Template;
        }
    }
}
