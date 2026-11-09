namespace LiteDB.Engine
{
    // Identity has no thread affinity. Legacy transaction lookup/completion still
    // checks the actual Thread separately; future handles can carry this token.
    internal sealed class TransactionOwnerToken
    {
    }
}
