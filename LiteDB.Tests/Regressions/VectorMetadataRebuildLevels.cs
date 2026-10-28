using System;
using LiteDB.Engine;

namespace LiteDB.Tests.Regressions
{
    // Pin valid level draws, rather than depending on runtime-specific Random sequences.
    // This sequence connects the published fixture's computed index. General HNSW
    // connectivity is a separate pre-existing limitation: https://github.com/litedb-org/LiteDB/issues/3141.
    // Tests run without collection parallelism; always restore the global test hook.
    internal sealed class VectorMetadataRebuildLevels : IDisposable
    {
        internal static readonly byte[] Sequence =
            { 4, 1, 1, 3, 2, 4, 1, 1, 1, 2, 1, 1, 1, 4, 1, 2, 2, 1, 1, 1 };

        private readonly Func<Random> _previous = VectorIndexService.LevelRandomFactory;

        internal VectorMetadataRebuildLevels() => VectorIndexService.LevelRandomFactory = () => new LevelRandom();

        public void Dispose() => VectorIndexService.LevelRandomFactory = _previous;

        private sealed class LevelRandom : Random
        {
            private int _node;
            private int _draw = 1;

            public override double NextDouble()
            {
                var level = Sequence[_node % Sequence.Length];
                if (_draw++ < level)
                {
                    // SampleLevel does not draw a terminating value at MaxLevels.
                    if (_draw == VectorIndexNode.MaxLevels) Advance();
                    return 0.25;
                }
                Advance();
                return 0.75;
            }

            private void Advance()
            {
                _node++;
                _draw = 1;
            }
        }
    }
}
