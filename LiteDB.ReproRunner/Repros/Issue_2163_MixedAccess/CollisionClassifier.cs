using LiteDB;

namespace Issue_2163_MixedAccess;

internal static class CollisionClassifier
{
    private static readonly string[] OwnershipTerms =
    {
        "already open",
        "another process",
        "being used",
        "connection mode",
        "exclusive",
        "lock",
        "mixed connection",
        "owner",
        "sharing violation",
        "temporarily unavailable"
    };

    public static bool IsExpectedOwnershipFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or UnauthorizedAccessException or LiteException &&
                OwnershipTerms.Any(term =>
                    current.Message.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
