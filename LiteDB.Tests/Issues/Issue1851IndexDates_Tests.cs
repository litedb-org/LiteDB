using System;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1851IndexDates_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Covered_projection_tracks_pragma_changes_before_and_after_reopen(bool reopen)
        {
            using var file = new TempFile();
            var instant = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["stamp"] = instant });
                rows.EnsureIndex("stamp");
                if (!reopen) Verify(db, instant);
            }
            if (reopen)
            {
                using var db = new LiteDatabase(file.Filename);
                Verify(db, instant);
            }
        }

        [Theory]
        [InlineData(5)]
        [InlineData(6)]
        public void Covered_dates_match_document_decoding_during_a_repeated_local_hour(int utcHour)
        {
            var instant = new DateTime(2024, 11, 3, utcHour, 30, 0, DateTimeKind.Utc);
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["stamp"] = instant.AddTicks(9999) });
            rows.EnsureIndex("stamp");
            foreach (var utc in new[] { false, true })
            {
                db.UtcDate = utc;
                var projected = rows.Query().OrderBy("stamp").Select("stamp").ToDocuments().Single()["stamp"].AsDateTime;
                var documentDate = rows.FindById(1)["stamp"].AsDateTime;
                Assert.Equal(documentDate, projected);
                Assert.Equal(documentDate.Kind, projected.Kind);
                if (utc) Assert.Equal(instant, projected);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Scalar_index_date_sentinels_match_document_decoding(bool maximum)
        {
            using var db = new LiteDatabase(":memory:");
            var date = maximum ? DateTime.MaxValue : DateTime.MinValue;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["stamp"] = date });
            rows.EnsureIndex("stamp");
            foreach (var utc in new[] { false, true })
            {
                db.UtcDate = utc;
                var projected = rows.Query().OrderBy("stamp").Select("stamp").ToDocuments().Single()["stamp"].AsDateTime;
                Assert.Equal(date, projected);
                Assert.Equal(DateTimeKind.Unspecified, projected.Kind);
                Assert.Equal(rows.FindById(1)["stamp"].AsDateTime, projected);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Nested_utc_index_dates_do_not_take_a_lossy_intermediate_local_conversion(bool nearMaximum)
        {
            using var db = new LiteDatabase(":memory:");
            var instant = nearMaximum ? new DateTime(9999, 12, 31, 23, 59, 0, DateTimeKind.Utc) :
                new DateTime(1, 1, 1, 0, 1, 0, DateTimeKind.Utc);
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("key");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["key"] = new BsonDocument { ["date"] = instant } });
            db.UtcDate = true;
            var query = rows.Query().OrderBy("key").Select("key");
            Assert.Equal("index", query.GetPlan()["lookup"]["loader"].AsString);
            var projected = query.ToDocuments().Single()["date"].AsDateTime;
            Assert.Equal(instant, projected);
            Assert.Equal(DateTimeKind.Utc, projected.Kind);
        }

        private static void Verify(LiteDatabase db, DateTime instant)
        {
            foreach (var utc in new[] { false, true, false })
            {
                db.UtcDate = utc;
                var query = db.GetCollection("rows").Query().OrderBy("stamp").Select("stamp");
                Assert.Equal("index", query.GetPlan()["lookup"]["loader"].AsString);
                var date = query.ToDocuments().Single()["stamp"].AsDateTime;
                Assert.Equal(utc ? DateTimeKind.Utc : DateTimeKind.Local, date.Kind);
                Assert.Equal(instant, date.ToUniversalTime());
            }
        }

        [Fact]
        public void Nested_index_key_dates_are_converted_without_mutating_the_cached_key()
        {
            using var db = new LiteDatabase(":memory:");
            var instant = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument
            {
                ["_id"] = 1,
                ["key"] = new BsonDocument { ["dates"] = new BsonArray { instant, DateTime.MinValue, DateTime.MaxValue } }
            });
            rows.EnsureIndex("key");
            foreach (var utc in new[] { true, false, true })
            {
                db.UtcDate = utc;
                var query = rows.Query().OrderBy("key").Select("key");
                Assert.Equal("index", query.GetPlan()["lookup"]["loader"].AsString);
                var dates = query.ToDocuments().Single()["dates"].AsArray;
                Assert.Equal(utc ? DateTimeKind.Utc : DateTimeKind.Local, dates[0].AsDateTime.Kind);
                Assert.Equal(instant, dates[0].AsDateTime.ToUniversalTime());
                Assert.Equal(DateTime.MinValue, dates[1].AsDateTime);
                Assert.Equal(DateTime.MaxValue, dates[2].AsDateTime);
                Assert.Equal(DateTimeKind.Unspecified, dates[1].AsDateTime.Kind);
                Assert.Equal(DateTimeKind.Unspecified, dates[2].AsDateTime.Kind);
            }
        }
    }
}
