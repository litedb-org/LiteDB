#if PROFILE
using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
namespace LiteDB
{
    [EventSource(Name = "LiteDB-SharedOpenProfile")]
    internal sealed class SharedOpenProfile : EventSource
    {
        internal static readonly SharedOpenProfile Log = new SharedOpenProfile();
        [Event(1)] public void Span(string name, double ms) { if (IsEnabled()) WriteEvent(1, name, ms); }
        internal static IDisposable Measure(string name) { var source = Log; return new Scope(name); }
        private sealed class Scope : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            internal Scope(string name) { _name = name; }
            public void Dispose() => Log.Span(_name, _watch.Elapsed.TotalMilliseconds);
        }
    }
}
#endif
