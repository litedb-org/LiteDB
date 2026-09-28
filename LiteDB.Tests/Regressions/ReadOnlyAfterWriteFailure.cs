using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 6 of docs/decisions/durability-policy.md: a write or sync failure is recorded and stops
    /// the engine, whose next call reopens it read-only from the files as they are. Reads keep working,
    /// <c>$database</c> reports the record without a write, and every write throws
    /// <see cref="LiteEngine.WriteFailedPrefix"/> followed by the record, before it changes anything.
    /// </summary>
    internal static class ReadOnlyAfterWriteFailure
    {
        /// <summary>
        /// <c>$database</c> of <paramref name="db"/> reports readOnly and a writeFailure of
        /// <paramref name="operation"/> on <paramref name="file"/> ("data", "log", or null when the
        /// failure named none) whose error starts with <paramref name="error"/>, with the log file
        /// kept (<paramref name="walKept"/>) or empty; readOnlyReason is that record. Returns the record.
        /// </summary>
        internal static string AssertReported(LiteDatabase db, string operation, string file, string error, bool walKept = true)
        {
            var info = db.GetCollection("$database").FindAll().Single();
            info["readOnly"].AsBoolean.Should().BeTrue("the engine continues read-only after a write failure");
            info["writeFailure"].IsDocument.Should().BeTrue("$database reports the failure without a write");
            var failure = info["writeFailure"].AsDocument;
            failure["operation"].AsString.Should().Be(operation);
            (failure["file"].IsNull ? null : failure["file"].AsString).Should().Be(file);
            failure["error"].AsString.Should().StartWith(error);
            failure["walKept"].AsBoolean.Should().Be(walKept);
            var time = failure["time"].AsDateTime.ToUniversalTime();
            var record = $"{operation} failed at {time:yyyy-MM-dd HH:mm:ss} UTC" + (file == null ? "" : $" on the {file} file") +
                $": {failure["error"].AsString} " + (walKept ? "The log file was kept." : "The log file was empty.");
            info["readOnlyReason"].AsString.Should().Be(record);
            return record;
        }

        /// <summary>
        /// <paramref name="write"/> throws the refusal that names <paramref name="record"/>; returns it
        /// (its inner exception is the recorded failure itself).
        /// </summary>
        internal static IOException AssertWriteRefused(Action write, string record)
        {
            var refused = write.Should().Throw<IOException>().Which;
            refused.Message.Should().Be(LiteEngine.WriteFailedPrefix + record);
            refused.InnerException.Should().NotBeNull("the refusal carries the recorded failure");
            return refused;
        }
    }
}
