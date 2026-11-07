using System;
using LiteDB.Engine;

namespace LiteDB.Client.Direct
{
    internal static class DirectHostSettings
    {
        internal static void RequireCompatible(EngineSettings a, EngineSettings b, string collation)
        {
            if (a.Password != b.Password || a.ReadOnly != b.ReadOnly || a.DurableCommits != b.DurableCommits ||
                a.CompactStorage != b.CompactStorage || a.LegacyIndexScan != b.LegacyIndexScan ||
                a.GetCacheSize() != b.GetCacheSize() || a.TransactionPageLimit != b.TransactionPageLimit ||
                a.InitialSize != b.InitialSize || a.IndexMigrationLimitSize != b.IndexMigrationLimitSize ||
                (b.Collation != null && b.Collation.ToString() != collation) || a.Upgrade != b.Upgrade || a.AutoRebuild != b.AutoRebuild ||
                a.RejectInvalidLocalTime != b.RejectInvalidLocalTime || !ReferenceEquals(a.ReadTransform, b.ReadTransform) ||
                !Equals(a.LocalTimeZone, b.LocalTimeZone))
                throw new InvalidOperationException("Incompatible Direct engine settings for an already-open physical database. Close all existing facades before changing settings.");
        }
    }
}
