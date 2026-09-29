using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests
{
    /// <summary>
    /// Assertions for decisions 3 and 6 of docs/decisions/durability-policy.md: a write or sync that
    /// failed is recorded, the engine continues read-only, <c>$database</c> reports the record without a
    /// write, and every later write throws with it before it changes anything.
    /// </summary>
    internal static class WriteFailureAssert
    {
        /// <summary>The error of a durable commit refused before it wrote a frame (decision 3).</summary>
        internal const string NotWritten = "This commit was not written: ";

        /// <summary>The error of a durable commit whose log stopped syncing after its proof (its frames reached the OS).</summary>
        internal const string OutcomeUnknown = "This commit's outcome is unknown: ";

        /// <summary>The error of a recovery barrier (checkpoint journal, conversion, promotion) whose log cannot sync.</summary>
        internal const string LogCannotSync = "The log file cannot sync to the device (#2242): ";

        /// <summary><see cref="NotWritten"/> because the log file answered "cannot sync".</summary>
        internal const string LogNotWritten = NotWritten + "the log file cannot sync to the device (#2242)";

        /// <summary><see cref="NotWritten"/> because the log file's directory answered "cannot sync" (decision 9).</summary>
        internal const string DirectoryNotWritten = NotWritten + "the log file's directory cannot sync to the device (#2242)";

        /// <summary>A write's error on an engine that opened read-only because its writable open was refused.</summary>
        internal const string OpenRefused = "Cannot modify this database: it opened read-only because the writable open was refused. ";

        /// <summary>
        /// <c>$database</c> reports the recorded failure: readOnly, writeFailure
        /// <c>{file, operation, error, time, walKept}</c>, and readOnlyReason, the record itself.
        /// Returns readOnlyReason, which every refused write carries.
        /// </summary>
        internal static string Recorded(LiteDatabase db, string operation, string file, string error, bool walKept)
        {
            var info = Info(db);
            info["readOnly"].AsBoolean.Should().BeTrue("the engine continues read-only after a write failure");
            var failure = info["writeFailure"].AsDocument;
            failure["operation"].AsString.Should().Be(operation);
            failure["file"].AsString.Should().Be(file);
            failure["error"].AsString.Should().StartWith(error);
            failure["time"].IsDateTime.Should().BeTrue();
            failure["walKept"].AsBoolean.Should().Be(walKept);
            var reason = info["readOnlyReason"].AsString;
            reason.Should().StartWith(operation + " failed at").And.Contain(failure["error"].AsString);
            return reason;
        }

        /// <summary>
        /// A durable commit refused before it wrote a frame (decision 3) was recorded as
        /// <c>{operation: "A commit", file: "log", walKept}</c>; returns readOnlyReason.
        /// </summary>
        internal static string CommitRefused(LiteDatabase db, string error = LogNotWritten, bool walKept = false) =>
            Recorded(db, "A commit", "log", error, walKept);

        /// <summary>
        /// <paramref name="write"/> throws, before it changes anything, the error that carries the record
        /// <paramref name="reason"/>, with the recorded failure as its inner exception.
        /// </summary>
        internal static void Refused(Action write, string reason)
        {
            var error = write.Should().Throw<IOException>().Which;
            error.Message.Should().Be(LiteEngine.WriteFailedPrefix + reason);
            error.InnerException.Should().NotBeNull("the recorded failure travels with every refused write");
            reason.Should().Contain(error.InnerException.Message);
        }

        /// <summary>No write or sync failed: <c>$database.writeFailure</c> is null.</summary>
        internal static void NoneRecorded(LiteDatabase db, string because = "")
        {
            var info = Info(db);
            info.ContainsKey("writeFailure").Should().BeTrue("$database reports the record");
            info["writeFailure"].IsNull.Should().BeTrue(because);
        }

        internal static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();
    }
}
