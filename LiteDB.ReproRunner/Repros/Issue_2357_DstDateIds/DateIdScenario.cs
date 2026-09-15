using LiteDB;

internal static class DateIdScenario
{
    public static int Run(string mode, string path)
    {
        try
        {
            var kind = mode == "utc" ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            var start = new DateTime(2006, 3, 23, 0, 0, 0, kind);
            var dates = Enumerable.Range(0, 480).Select(hour => Row(start.AddHours(hour), hour)).ToArray();
            Require(dates.Select(row => row.DateHour.Ticks).Distinct().Count() == 480,
                "the external input must contain 480 distinct wall-clock IDs");
            var eastern = Environment.GetEnvironmentVariable("TZ") == "America/New_York";
            var missingHour = new DateTime(2006, 4, 2, 2, 0, 0, DateTimeKind.Unspecified);
            Require(TimeZoneInfo.Local.IsInvalidTime(missingHour) == eastern, "the requested timezone was not applied");
            Require(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2006, 4, 2, 1, 0, 0)) ==
                (eastern ? TimeSpan.FromHours(-5) : TimeSpan.Zero), "unexpected pre-transition offset");
            var target = eastern && kind == DateTimeKind.Unspecified;
            Console.WriteLine($"DATE_PRECONDITIONS_2357: kind={kind}, zone={TimeZoneInfo.Local.Id}, unique=480");

            var baseline = Row(start.AddHours(-1), -1);
            var followup = Row(start.AddHours(481), 481);
            Exception? failure = null;
            using (var database = new LiteDatabase(path))
            {
                database.UtcDate = kind == DateTimeKind.Utc;
                var rows = database.GetCollection<HourlyRow>("hours");
                rows.Insert(baseline);
                Require(rows.EnsureIndex(row => row.Hour, true), "secondary index was not created");
                database.Checkpoint();
                try
                {
                    Require(rows.InsertBulk(dates) == dates.Length, "bulk insert did not acknowledge every input");
                }
                catch (Exception error)
                {
                    failure = error;
                }

                AssertLedger(database, failure == null ? new[] { baseline }.Concat(dates).ToArray() : new[] { baseline });
                rows.Insert(followup);
                database.Checkpoint();
            }
            using (var reopened = new LiteDatabase(path))
            {
                reopened.UtcDate = kind == DateTimeKind.Utc;
                AssertLedger(reopened, failure == null
                    ? new[] { baseline }.Concat(dates).Append(followup).ToArray()
                    : new[] { baseline, followup });
            }

            if (failure == null)
            {
                Console.WriteLine("VERIFIED_DATE_IDS_2357: every acknowledged hour and payload survived reopen");
                return 10;
            }
            if (target && failure is LiteException lite && lite.ErrorCode == LiteException.INDEX_DUPLICATE_KEY &&
                lite.Message.Contains("2006-04-02T07:00:00") && lite.Message.Contains("_id"))
            {
                Console.WriteLine("DUPLICATE_DATE_2357: unique unspecified IDs became duplicate 2006-04-02T07:00:00Z; committed baseline and post-failure write survived");
                return 0;
            }

            var message = failure.Message.ToLowerInvariant();
            if (target && (failure is ArgumentException || failure is LiteException) &&
                message.Contains("invalid") && (message.Contains("local time") || message.Contains("datetime")))
            {
                Console.WriteLine("VERIFIED_DATE_IDS_2357: nonexistent local hour explicitly rejected and bulk insert rolled back");
                return 10;
            }
            throw new InvalidOperationException("An unrelated failure cannot prove this issue.", failure);
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 20;
        }
    }

    private static void AssertLedger(LiteDatabase database, HourlyRow[] expected)
    {
        var rows = database.GetCollection<HourlyRow>("hours");
        var actual = rows.FindAll().OrderBy(row => row.Hour).ToArray();
        Require(actual.Length == expected.Length && rows.Count() == expected.Length, "row count disagrees with the receipt ledger");
        Require(actual.Select(row => row.Hour).SequenceEqual(expected.Select(row => row.Hour)), "missing or unexpected hours");
        for (var index = 0; index < expected.Length; index++)
        {
            var receipt = expected[index];
            AssertRow(actual[index], receipt);
            AssertRow(rows.FindById(receipt.DateHour), receipt);
            var indexed = rows.Find(row => row.Hour == receipt.Hour).ToArray();
            Require(indexed.Length == 1, "secondary index does not identify exactly one receipt");
            AssertRow(indexed[0], receipt);
        }
    }

    private static void AssertRow(HourlyRow? actual, HourlyRow expected)
    {
        Require(actual != null && actual.DateHour.Ticks == expected.DateHour.Ticks &&
            actual.Hour == expected.Hour && actual.Proof == expected.Proof && actual.Payload == expected.Payload,
            "timestamp or payload differs from the external receipt");
    }

    private static HourlyRow Row(DateTime date, int hour)
    {
        return new HourlyRow
        {
            DateHour = date,
            Hour = hour,
            Proof = (hour * 7919) ^ 0x2357,
            Payload = $"hour:{hour}:" + new string((char)('A' + (hour + 26) % 26), 67)
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public sealed class HourlyRow
    {
        [BsonId]
        public DateTime DateHour { get; set; }
        public int Hour { get; set; }
        public int Proof { get; set; }
        public string Payload { get; set; } = string.Empty;
    }
}
