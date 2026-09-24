using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    // The duplicate key in #2357 is correct: DateTime keys are UTC instants and two local times collapsed into one.
    // The report exists because the error did not say so. The explanation must appear for that case only, and it must
    // not depend on the machine time zone: the zone is injected.
    public class Issue2357DuplicateKeyHint_Tests
    {
        private const string Plain = "Cannot insert duplicate key in unique index '_id'. The duplicate value is '";

        private static readonly TimeZoneInfo _zone = Issue2357InvalidTime_Tests.DstZone;

        private static string Message(BsonValue key) => LiteException.IndexDuplicateKey("_id", key, _zone).Message;

        [Theory]
        [InlineData(2, 30, "the skipped hour itself")]
        [InlineData(3, 30, "the hour the skipped one collapses onto: in an ascending insert THIS key is the reported duplicate")]
        public void Keys_around_the_spring_gap_explain_the_collision(int hour, int minute, string why)
        {
            var message = Message(new DateTime(2006, 4, 2, hour, minute, 0));

            message.Should().StartWith(Plain, why);
            message.Should().Contain("daylight saving").And.Contain("UtcDate").And.Contain("RejectInvalidLocalTime");
        }

        [Fact]
        public void The_repeated_autumn_hour_explains_the_collision()
        {
            Message(new DateTime(2006, 10, 29, 1, 30, 0)).Should().Contain("daylight saving");
        }

        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Local)]
        public void An_ordinary_local_duplicate_keeps_the_plain_message(DateTimeKind kind)
        {
            var key = new BsonValue(new DateTime(2006, 6, 1, 12, 0, 0, kind));

            Message(key).Should().Be(Plain + key.ToString() + "'.");
        }

        [Fact]
        public void A_UTC_key_in_the_gap_hour_keeps_the_plain_message()
        {
            Message(new DateTime(2006, 4, 2, 2, 30, 0, DateTimeKind.Utc)).Should().NotContain("daylight saving");
        }

        [Fact]
        public void A_key_of_another_type_keeps_the_plain_message()
        {
            Message(7).Should().Be(Plain + "7'.");
        }

        [Fact]
        public void The_engine_reports_the_same_error_code_and_field()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("when", "$.when", true);
            rows.Insert(new BsonDocument { ["_id"] = 1, ["when"] = new DateTime(2006, 6, 1, 12, 0, 0) });

            Action again = () => rows.Insert(new BsonDocument { ["_id"] = 2, ["when"] = new DateTime(2006, 6, 1, 12, 0, 0) });

            var error = again.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
            error.Message.Should().StartWith("Cannot insert duplicate key in unique index 'when'.");
        }
    }
}
