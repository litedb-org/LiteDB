using System;

namespace LiteDB.Engine
{
    /// <summary>
    /// Invalidates cached storage before a destructive operation, including its
    /// lease scan. The caller still owns the database/engine exclusion required
    /// by that operation. Failed operations invalidate the previous fence too.
    /// </summary>
    internal readonly struct StructuralScope : IDisposable
    {
        private readonly ICoordinationSignals _signals;

        internal StructuralScope(ICoordinationSignals signals)
        {
            _signals = signals;
            _signals?.StructuralBegin();
        }

        public void Dispose() => _signals?.StructuralEnd(-1);
    }
}
