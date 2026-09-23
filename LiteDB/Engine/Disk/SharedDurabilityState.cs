namespace LiteDB.Engine
{
    /// <summary>
    /// A shared connection's diagnostic survives its short-lived file engines.
    /// This does not suppress subsequent engines' attempts to sync to the device.
    /// </summary>
    internal sealed class SharedDurabilityState
    {
        internal volatile bool Degraded;
    }
}
