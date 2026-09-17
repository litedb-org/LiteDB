using System;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2357InvalidTime_Tests
    {
        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Local)]
        [InlineData(DateTimeKind.Utc)]
        public void Date_writers_reject_only_nonexistent_local_times(DateTimeKind kind)
        {
            // Run this fixture under America/New_York as well as UTC. No global
            // timezone mutation is needed inside the parallel test process.
            var wallClock = new DateTime(2006, 4, 2, 2, 30, 0);
            var date = DateTime.SpecifyKind(wallClock, kind);
            var invalid = kind != DateTimeKind.Utc && TimeZoneInfo.Local.IsInvalidTime(wallClock);
            Action bson = () => BsonSerializer.Serialize(new BsonDocument { ["date"] = date });
            Action ticks = () =>
            {
                using var writer = new BufferWriter(new byte[8]);
                writer.Write(date);
            };
            Action index = () => new BufferSlice(new byte[8], 0, 8).Write(date, 0);
            foreach (var write in new[] { bson, ticks, index })
            {
                if (invalid) write.Should().Throw<ArgumentException>().WithMessage("*Invalid local time*");
                else write.Should().NotThrow();
            }
        }

        [Fact]
        public void Invalid_hour_rolls_back_batch_and_database_remains_usable()
        {
            var invalid = new DateTime(2006, 4, 2, 2, 0, 0);
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("hours");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["date"] = invalid.AddHours(-2) });
                rows.EnsureIndex("date", true);
                Action insert = () => rows.Insert(new[]
                {
                    new BsonDocument { ["_id"] = 2, ["date"] = invalid.AddHours(-1) },
                    new BsonDocument { ["_id"] = 3, ["date"] = invalid }
                });
                if (TimeZoneInfo.Local.IsInvalidTime(invalid))
                {
                    insert.Should().Throw<ArgumentException>().WithMessage("*Invalid local time*");
                    rows.Count().Should().Be(1);
                }
                else
                {
                    insert.Should().NotThrow();
                    rows.Count().Should().Be(3);
                }
                rows.Insert(new BsonDocument { ["_id"] = 4, ["date"] = invalid.AddHours(1) });
            }
            using var reopened = new LiteDatabase(file.Filename);
            Assert.NotNull(reopened.GetCollection("hours").FindById(1));
            Assert.NotNull(reopened.GetCollection("hours").FindById(4));
        }
    }
}
