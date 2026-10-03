using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace LiteDB
{
    /// <summary>Connection-string form of the Shared writer-wait options, in both directions.</summary>
    internal static class SharedWaitConnectionOptions
    {
        private const string WriterTimeout = "shared writer timeout";
        private const string SelfWaitGrace = "shared self wait grace";

        internal static bool IsKey(string key) =>
            key.Equals(WriterTimeout, StringComparison.OrdinalIgnoreCase) || key.Equals(SelfWaitGrace, StringComparison.OrdinalIgnoreCase);

        internal static void Read(Dictionary<string, string> values, ConnectionString target)
        {
            target.SharedWriterTimeout = values.GetTimeout(WriterTimeout, target.SharedWriterTimeout);
            target.SharedSelfWaitGrace = values.GetTimeout(SelfWaitGrace, target.SharedSelfWaitGrace);
        }

        /// <summary>Write configured (non-infinite) values, so that parsing the result preserves them.</summary>
        internal static void Append(StringBuilder builder, ConnectionString source)
        {
            Append(builder, WriterTimeout, source.SharedWriterTimeout);
            Append(builder, SelfWaitGrace, source.SharedSelfWaitGrace);
        }

        private static void Append(StringBuilder builder, string key, TimeSpan value)
        {
            if (value == Timeout.InfiniteTimeSpan) return;
            builder.Append(key).Append('=').Append(value.ToString("c", CultureInfo.InvariantCulture)).Append(';');
        }
    }
}
