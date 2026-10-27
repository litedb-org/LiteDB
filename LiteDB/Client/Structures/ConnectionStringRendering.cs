using System;
using System.Globalization;
using System.Text;

namespace LiteDB
{
    internal static class ConnectionStringRendering
    {
        internal static string Build(ConnectionString connection, bool includePlaintextPassword, bool explicitTransactionPages)
        {
            var bld = new StringBuilder();

            if (!string.IsNullOrEmpty(connection.Filename))
            {
                bld.Append("Filename=");
                AppendQuotedString(bld, connection.Filename);
                bld.Append(';');
            }

            var fileNameLength = bld.Length;

            if (connection.IndexMigrationLimitSize.HasValue)
                bld.Append("index migration limit size=").AppendFormat(CultureInfo.InvariantCulture, "{0:D}", connection.IndexMigrationLimitSize.Value).Append(';');

            if (connection.Connection != ConnectionType.Direct)
            {
                bld.Append("Connection=")
                    .Append(connection.Connection)
                    .Append(';');
            }

            if (connection.Password != null)
            {
                bld.Append("Password=");
                if (includePlaintextPassword)
                {
                    AppendQuotedString(bld, connection.Password);
                }
                else
                {
                    bld.Append('*', 8);
                }

                bld.Append(';');
            }

            if (connection.InitialSize != 0)
            {
                bld.Append("InitialSize=")
                    .AppendFormat(CultureInfo.InvariantCulture, "{0:D}", connection.InitialSize)
                    .Append(';');
            }

            if (connection.ReadOnly)
            {
                bld.Append("ReadOnly=")
                    .Append(connection.ReadOnly)
                    .Append(';');
            }

            if (connection.LegacyIndexScan)
            {
                bld.Append("legacy index scan=true;");
            }

            if (connection.Collation != null)
            {
                bld.Append("Collation=")
                    .Append(connection.Collation)
                    .Append(';');
            }

            if (connection.Upgrade)
            {
                bld.Append("Upgrade=")
                    .Append(connection.Upgrade)
                    .Append(';');
            }

            if (connection.AutoRebuild)
            {
                bld.Append("Auto-Rebuild=")
                    .Append(connection.AutoRebuild)
                    .Append(';');
            }

            if (connection.RejectInvalidLocalTime)
            {
                bld.Append("Reject Invalid Local Time=")
                    .Append(connection.RejectInvalidLocalTime)
                    .Append(';');
            }

            if (connection.CompactStorage != CompactStorageMode.Auto)
            {
                bld.Append("Compact Storage=")
                    .Append(connection.CompactStorage)
                    .Append(';');
            }

            if (connection.DurableCommits == false)
            {
                bld.Append("Durable Commits=")
                    .Append(connection.DurableCommits)
                    .Append(';');
            }

            if (connection.WaitForSameThreadTransaction)
                bld.Append("Wait For Same Thread Transaction=true;");

            if (connection.SharedOneFile) bld.Append("Shared One File=true;");
            if (connection.SharedMemorySize != 0) bld.Append("Shared Memory Size=").Append(connection.SharedMemorySize.ToString(CultureInfo.InvariantCulture)).Append(';');
            if (connection.SharedProcessCapacity != 256) bld.Append("Shared Process Capacity=").Append(connection.SharedProcessCapacity.ToString(CultureInfo.InvariantCulture)).Append(';');
            if (connection.SharedGenerationCapacity != 256) bld.Append("Shared Generation Capacity=").Append(connection.SharedGenerationCapacity.ToString(CultureInfo.InvariantCulture)).Append(';');

            if (connection.MemoryProfile != MemoryProfile.Balanced)
            {
                bld.Append("Memory Profile=")
                    .Append(connection.MemoryProfile)
                    .Append(';');
            }

            if (connection.CacheSize != 0)
            {
                bld.Append("Cache Size=")
                    .AppendFormat(CultureInfo.InvariantCulture, "{0:D}", connection.CacheSize)
                    .Append(';');
            }

            if (explicitTransactionPages)
            {
                bld.Append("Transaction Pages=")
                    .AppendFormat(CultureInfo.InvariantCulture, "{0:D}", connection.TransactionPageLimit)
                    .Append(';');
            }

            if (bld.Length == fileNameLength &&
                !string.IsNullOrEmpty(connection.Filename) &&
                connection.Filename == connection.Filename.Trim() &&
                !connection.Filename.Contains("="))
            {
                return connection.Filename;
            }

            if (bld.Length > 0)
            {
                bld.Length--; // ;
            }

            return bld.ToString();
        }

        private static void AppendQuotedString(StringBuilder target, string str)
        {
            target.Append('"');

            var backslashCount = 0;

            foreach (var chr in str ?? string.Empty)
            {
                if (chr == '\\')
                {
                    backslashCount++;
                    continue;
                }

                target.Append('\\', chr == '"' ? backslashCount * 2 + 1 : backslashCount);
                target.Append(chr);
                backslashCount = 0;
            }

            target.Append('\\', backslashCount * 2);
            target.Append('"');
        }
    }
}
