using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine;

// The existing CI FullyQualifiedName~CrossProcess_Shared_Tests filter includes these controls.
public class CrossProcess_Shared_Tests_Oracle
{
    private static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddSeconds(1);

    [Fact]
    public void CompleteRowsAndActualSecondaryIndex_Pass()
    {
        using var stream = new MemoryStream();
        using var db = new LiteDatabase(stream);
        var collection = db.GetCollection("rows");
        collection.Insert(Rows());
        collection.EnsureIndex("task_id");
        SharedWriteOracle.Verify(collection, 2, 3, Start, End);
    }

    [Fact]
    public void MissingSecondaryIndex_CannotPassByScanning()
    {
        using var stream = new MemoryStream();
        using var db = new LiteDatabase(stream);
        var collection = db.GetCollection("rows");
        collection.Insert(Rows());
        Action verify = () => SharedWriteOracle.Verify(collection, 2, 3, Start, End);
        verify.Should().Throw<InvalidOperationException>().WithMessage("*secondary index seek*");
    }

    [Theory]
    [InlineData("duplicate-ordinal", "*every ordinal exactly once*")]
    [InlineData("payload", "*payload differs*")]
    [InlineData("duplicate-id", "*IDs must be unique*")]
    [InlineData("ordinal-type", "*must be Int32*")]
    [InlineData("timestamp-type", "*BSON DateTime type*")]
    [InlineData("timestamp-value", "*outside the worker run*")]
    [InlineData("extra-field", "*field shape differs*")]
    public void SameCounts_DoNotHideDamagedRows(string damage, string diagnostic)
    {
        var rows = Rows();
        switch (damage)
        {
            case "duplicate-ordinal":
                rows[1]["doc_number"] = 0;
                rows[1]["data"] = "Data from task 1, document 0";
                break;
            case "payload": rows[1]["data"] = "damaged"; break;
            case "duplicate-id": rows[1]["_id"] = rows[0]["_id"]; break;
            case "ordinal-type": rows[1]["doc_number"] = 1L; break;
            case "timestamp-type": rows[1]["timestamp"] = Start.ToString("O"); break;
            case "timestamp-value": rows[1]["timestamp"] = Start.AddDays(-1); break;
            case "extra-field": rows[1]["unexpected"] = true; break;
        }
        rows.Length.Should().Be(6);
        rows.Count(row => row["task_id"].AsInt32 == 1).Should().Be(3);
        Action verify = () => Verify(rows, worker => rows.Where(row => row["task_id"].AsInt32 == worker).ToArray());
        verify.Should().Throw<InvalidOperationException>().WithMessage(diagnostic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameIndexCounts_DoNotHideWrongMembershipOrDuplicates(bool duplicate)
    {
        var rows = Rows();
        Action verify = () => Verify(rows, worker =>
        {
            var result = rows.Where(row => row["task_id"].AsInt32 == worker).ToArray();
            if (worker == 1) result[0] = duplicate ? result[1] : rows[3];
            result.Length.Should().Be(3);
            return result;
        });
        verify.Should().Throw<InvalidOperationException>().WithMessage("*secondary-index documents differ*");
    }

    [Fact]
    public void IndexPayloadDamage_IsRejectedEvenWhenIdsAndCountsMatch()
    {
        var rows = Rows();
        Action verify = () => Verify(rows, worker => rows.Where(row => row["task_id"].AsInt32 == worker).Select(row =>
        {
            var copy = BsonSerializer.Deserialize(BsonSerializer.Serialize(row));
            copy["timestamp"] = Start.AddMilliseconds(900);
            return copy;
        }).ToArray());
        verify.Should().Throw<InvalidOperationException>().WithMessage("*secondary-index documents differ*");
    }

    private static void Verify(BsonDocument[] rows, Func<int, BsonDocument[]> indexed) =>
        SharedWriteOracle.VerifyResults(rows, indexed, 2, 3, Start, End);

    private static BsonDocument[] Rows() => Enumerable.Range(1, 2).SelectMany(worker => Enumerable.Range(0, 3)
        .Select(ordinal => new BsonDocument
        {
            ["_id"] = new ObjectId((worker * 10 + ordinal).ToString("x24")),
            ["task_id"] = worker, ["doc_number"] = ordinal,
            ["timestamp"] = Start.AddMilliseconds(500),
            ["data"] = $"Data from task {worker}, document {ordinal}"
        })).ToArray();
}
