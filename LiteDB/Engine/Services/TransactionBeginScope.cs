using System;
using System.Threading;

namespace LiteDB.Engine
{
    // Only pending public begins carry cancellation. The token contains no facade,
    // mapper or callback graph and can safely cross to a native holder thread.
    internal sealed class TransactionBeginScope : IDisposable
    {
        [ThreadStatic] private static TransactionBeginScope _current;
        private readonly TransactionBeginScope _previous;
        internal static CancellationToken Token => _current?._token ?? CancellationToken.None;
        private readonly CancellationToken _token;

        internal TransactionBeginScope(CancellationToken token)
        {
            _previous = _current;
            _token = token;
            _current = this;
        }

        internal static void Check() => Check(Token);
        internal static void Check(CancellationToken token)
        {
            if (token.IsCancellationRequested) throw new ObjectDisposedException(nameof(LiteDatabase));
        }

        public void Dispose() => _current = _previous;
    }
}
