using System;

namespace LiteDB.Engine
{
    /// <summary>
    /// A writable open of storage that cannot be written (<see cref="EngineSettings.ReadOnlyStorage"/>)
    /// reached a step that would change it: recovery, repair, trimming, migration or conversion.
    /// Thrown before that step writes anything; the engine then opens the storage read-only.
    /// </summary>
    internal sealed class ReadOnlyOpenRequiredException : Exception
    {
    }
}
