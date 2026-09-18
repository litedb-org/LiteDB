using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2357InvalidTime_Tests
    {
        // 02:00-03:00 does not exist on 2006-04-02 and 01:00-02:00 happens twice on 2006-10-29 in this zone.
        // A custom zone keeps the tests independent of the machine time zone and of per-OS zone ids.
        internal static readonly TimeZoneInfo DstZone = CreateDstZone();

        internal static readonly DateTime Gap = new DateTime(2006, 4, 2, 2, 30, 0);

        private static readonly DateTime _ambiguous = new DateTime(2006, 10, 29, 1, 30, 0);

        private static TimeZoneInfo CreateDstZone()
        {
            var twoOClock = new DateTime(1, 1, 1, 2, 0, 0);
            var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(twoOClock, 4, 1, DayOfWeek.Sunday);
            var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(twoOClock, 10, 5, DayOfWeek.Sunday);
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);

            return TimeZoneInfo.CreateCustomTimeZone("LiteDB DST", TimeSpan.FromHours(-5), "LiteDB DST", "Standard", "Daylight", new[] { rule });
        }

        internal static LiteDatabase Open(bool reject, string filename = ":memory:", ConnectionType connection = ConnectionType.Direct)
        {
            var connectionString = new ConnectionString { Filename = filename, Connection = connection, RejectInvalidLocalTime = reject };

            return new LiteDatabase(connectionString.CreateEngine(settings => settings.LocalTimeZone = DstZone));
        }

        private static void ShouldReject(Action write)
        {
            write.Should().Throw<ArgumentException>()
                .WithMessage("Invalid local time cannot be stored as a UTC DateTime. Supply a valid local time or an explicit UTC value.*");
        }

        [Fact]
        public void Test_zone_has_the_expected_gap_and_overlap()
        {
            DstZone.IsInvalidTime(Gap).Should().BeTrue();
            DstZone.IsInvalidTime(_ambiguous).Should().BeFalse();
            DstZone.IsAmbiguousTime(_ambiguous).Should().BeTrue();
        }

        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Local)]
        [InlineData(DateTimeKind.Utc)]
        public void Low_level_date_writers_never_reject_a_nonexistent_local_time(DateTimeKind kind)
        {
            var date = DateTime.SpecifyKind(Gap, kind);
            Action bson = () => BsonSerializer.Serialize(new BsonDocument { ["date"] = date });
            Action json = () => JsonSerializer.Serialize(new BsonDocument { ["date"] = date });
            Action ticks = () =>
            {
                using var writer = new BufferWriter(new byte[8]);
                writer.Write(date);
            };
            Action index = () => new BufferSlice(new byte[8], 0, 8).Write(date, 0);

            foreach (var write in new[] { bson, json, ticks, index })
            {
                write.Should().NotThrow();
            }
        }

        [Fact]
        public void Switch_is_off_by_default()
        {
            new EngineSettings().RejectInvalidLocalTime.Should().BeFalse();
            new ConnectionString().RejectInvalidLocalTime.Should().BeFalse();
            new ConnectionString("filename=demo.db").RejectInvalidLocalTime.Should().BeFalse();
        }

        [Fact]
        public void Off_stores_the_gap_time_shifted_and_the_explicit_transaction_commits_every_row()
        {
            using var file = new TempFile();
            using (var db = Open(false, file.Filename))
            {
                var rows = db.GetCollection("hours");

                db.BeginTrans().Should().BeTrue();
                rows.Insert(new BsonDocument { ["_id"] = 1, ["date"] = Gap.AddHours(-2) });
                rows.Insert(new BsonDocument { ["_id"] = 2, ["date"] = Gap, ["nested"] = new BsonDocument { ["items"] = new BsonArray { Gap } } });
                rows.Upsert(new BsonDocument { ["_id"] = 3, ["date"] = DateTime.SpecifyKind(Gap, DateTimeKind.Local) });
                rows.Update(new BsonDocument { ["_id"] = 1, ["date"] = Gap });
                db.Commit().Should().BeTrue();
            }

            using var reopened = Open(false, file.Filename);
            var stored = reopened.GetCollection("hours");

            stored.Count().Should().Be(3);
            // whatever the machine zone does to the wall clock (nothing, or a shift out of its own gap) is what is stored
            stored.FindAll().Select(x => x["date"].AsDateTime).Should().OnlyContain(x => x == Gap.ToUniversalTime().ToLocalTime());
        }

        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Local)]
        public void On_rejects_a_gap_time_at_top_level_nested_and_as_id(DateTimeKind kind)
        {
            var gap = DateTime.SpecifyKind(Gap, kind);
            using var db = Open(true);
            var rows = db.GetCollection("hours");

            ShouldReject(() => rows.Insert(new BsonDocument { ["_id"] = 1, ["date"] = gap }));
            ShouldReject(() => rows.Insert(new BsonDocument { ["_id"] = 2, ["a"] = new BsonDocument { ["b"] = new BsonArray { 1, new BsonDocument { ["c"] = gap } } } }));
            ShouldReject(() => rows.Insert(new BsonDocument { ["_id"] = gap }));
            ShouldReject(() => rows.Insert(new BsonDocument { ["_id"] = new BsonDocument { ["key"] = gap } }));

            rows.Count().Should().Be(0);
        }

        [Fact]
        public void On_rejects_update_upsert_bulk_and_update_many()
        {
            using var db = Open(true);
            var rows = db.GetCollection("hours");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["date"] = Gap.AddHours(-2) });

            ShouldReject(() => rows.Update(new BsonDocument { ["_id"] = 1, ["date"] = Gap }));
            ShouldReject(() => rows.Upsert(new BsonDocument { ["_id"] = 1, ["date"] = Gap }));
            ShouldReject(() => rows.Upsert(new BsonDocument { ["_id"] = 2, ["date"] = Gap }));
            ShouldReject(() => rows.InsertBulk(new[]
            {
                new BsonDocument { ["_id"] = 3, ["date"] = Gap.AddHours(-1) },
                new BsonDocument { ["_id"] = 4, ["date"] = Gap }
            }));
            ShouldReject(() => rows.UpdateMany(BsonExpression.Create("{ date: @0 }", Gap), BsonExpression.Create("_id = 1")));

            rows.Count().Should().Be(1);
            rows.FindById(1)["date"].AsDateTime.Should().Be(Gap.AddHours(-2).ToUniversalTime().ToLocalTime());
        }

        [Fact]
        public void On_rejects_file_storage_metadata()
        {
            using var db = Open(true);
            db.FileStorage.Upload("file", "file.bin", new MemoryStream(new byte[] { 1, 2, 3 }));

            ShouldReject(() => db.FileStorage.SetMetadata("file", new BsonDocument { ["taken"] = Gap }));
        }

        [Fact]
        public void On_accepts_utc_min_max_ambiguous_and_valid_values()
        {
            using var db = Open(true);
            var rows = db.GetCollection("hours");

            rows.Insert(new BsonDocument
            {
                ["_id"] = DateTime.SpecifyKind(Gap, DateTimeKind.Utc),
                ["utc"] = DateTime.SpecifyKind(Gap, DateTimeKind.Utc),
                ["min"] = DateTime.MinValue,
                ["max"] = DateTime.MaxValue,
                ["ambiguous"] = _ambiguous,
                ["valid"] = new BsonArray { Gap.AddHours(-1), Gap.AddHours(1) }
            });

            rows.Count().Should().Be(1);
        }

        [Fact]
        public void On_never_rejects_a_query_parameter()
        {
            using var db = Open(true);
            var rows = db.GetCollection("hours");
            rows.Insert(new BsonDocument { ["_id"] = Gap.AddHours(-1), ["date"] = Gap.AddHours(-1) });
            rows.EnsureIndex("date");

            rows.Find(Query.EQ("date", Gap)).ToList();
            rows.Find(Query.GTE("_id", Gap)).ToList();
            rows.Find(BsonExpression.Create("date = @0 OR other < @0", Gap)).ToList();
            rows.FindById(Gap);
            rows.DeleteMany("date = @0", Gap);
            rows.Delete(Gap);

            rows.Count().Should().Be(1);
        }

        [Fact]
        public void On_failure_rolls_back_the_batch_and_the_database_remains_usable()
        {
            using var file = new TempFile();
            using (var db = Open(true, file.Filename))
            {
                var rows = db.GetCollection("hours");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["date"] = Gap.AddHours(-2) });
                rows.EnsureIndex("date", true);

                ShouldReject(() => rows.Insert(new[]
                {
                    new BsonDocument { ["_id"] = 2, ["date"] = Gap.AddHours(-1) },
                    new BsonDocument { ["_id"] = 3, ["date"] = Gap }
                }));

                rows.Count().Should().Be(1);
                rows.Insert(new BsonDocument { ["_id"] = 4, ["date"] = Gap.AddHours(1) });
            }

            using var reopened = Open(true, file.Filename);
            reopened.GetCollection("hours").FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 4 });
        }

        [Fact]
        public void Shared_connection_honours_the_switch()
        {
            using var file = new TempFile();
            using var db = Open(true, file.Filename, ConnectionType.Shared);
            var rows = db.GetCollection("hours");

            ShouldReject(() => rows.Insert(new BsonDocument { ["_id"] = 1, ["date"] = Gap }));
            rows.Insert(new BsonDocument { ["_id"] = 2, ["date"] = Gap.AddHours(1) });

            rows.Count().Should().Be(1);
        }

        [Theory]
        [InlineData("filename=demo.db;reject invalid local time=true")]
        [InlineData("Filename=demo.db;Reject Invalid Local Time=True")]
        public void Connection_string_key_is_parsed_and_reaches_the_engine_settings(string text)
        {
            var parsed = new ConnectionString(text) { Filename = ":memory:" };
            EngineSettings captured = null;

            parsed.RejectInvalidLocalTime.Should().BeTrue();
            using (parsed.CreateEngine(settings => captured = settings))
            {
                captured.RejectInvalidLocalTime.Should().BeTrue();
            }
        }

        [Fact]
        public void Connection_string_round_trips_the_switch()
        {
            var on = new ConnectionString { Filename = "demo.db", RejectInvalidLocalTime = true };

            on.ToString().Should().Be("Filename=\"demo.db\";Reject Invalid Local Time=True");
            new ConnectionString(on.ToString()).RejectInvalidLocalTime.Should().BeTrue();
            new ConnectionString("reject invalid local time=true").RejectInvalidLocalTime.Should().BeTrue();
            new ConnectionString { Filename = "demo.db" }.ToString().Should().Be("demo.db");
        }
    }
}
