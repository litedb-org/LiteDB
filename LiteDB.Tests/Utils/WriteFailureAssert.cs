using System.Linq;
using FluentAssertions;

namespace LiteDB.Tests
{
    /// <summary>
    /// Assertions for decision 6 of docs/decisions/durability-policy.md: a write or sync that failed is
    /// recorded before the stop it causes, and <c>$database.writeFailure</c> reports the record; storage
    /// that answers "cannot sync" (#2242) records nothing.
    /// </summary>
    internal static class WriteFailureAssert
    {
        /// <summary>
        /// <c>$database.writeFailure</c> reports the recorded failure <c>{file, operation, error, time, walKept}</c>.
        /// </summary>
        internal static void Recorded(LiteDatabase db, string operation, string file, string error, bool walKept)
        {
            var recorded = Info(db)["writeFailure"];
            recorded.IsDocument.Should().BeTrue("a write or sync failure is recorded");
            var failure = recorded.AsDocument;
            failure["operation"].AsString.Should().Be(operation);
            failure["file"].AsString.Should().Be(file);
            failure["error"].AsString.Should().StartWith(error);
            failure["time"].IsDateTime.Should().BeTrue();
            failure["walKept"].AsBoolean.Should().Be(walKept);
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
