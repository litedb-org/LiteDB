using System;

namespace LiteDB.Client.Shared
{
    internal interface ISharedReaderRegistry : IDisposable
    {
        IDisposable Register(int version);
        IDisposable RegisterUnscanned(int version);
        void Probe(int version);
        int? OldestVersion();
        int[] LiveVersions();
    }

    internal sealed class SharedGenerationCapacityException : System.IO.IOException
    {
        internal SharedGenerationCapacityException() : base("Shared distinct reader-generation capacity exhausted before query execution.") { }
    }
}
