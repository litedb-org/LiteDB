namespace LiteDB
{
    // Internal until the public handle API is introduced. Outcome survives cleanup
    // disposal; Committed reflects the configured durability contract, including
    // its explicit DurableCommits=false degradation.
    internal enum LiteTransactionState
    {
        Active, Committed, RolledBack, Failed, Indeterminate
    }
}
