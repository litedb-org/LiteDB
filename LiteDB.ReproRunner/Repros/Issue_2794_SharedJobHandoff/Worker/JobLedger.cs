using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace Issue2794
{
    // The nullable-date handoff follows #1742. Patterned payloads/dictionaries
    // are independent controls that make valid updates grow and shrink.
    internal static class JobLedger
    {
        internal const int Rows = 24;
        internal const int Rounds = 24;
        private static readonly DateTime Epoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly int[] Sizes = { 96, 7800, 8300, 16000, 41000 };

        internal static Job Expected(int id, int round, bool processed)
        {
            var stamp = Epoch.AddDays(id).AddSeconds(round * 10);
            var phase = round == 0 ? 0 : processed ? round * 2 : round * 2 - 1;
            return new Job
            {
                Id = Key(id),
                Round = round,
                Status = round == 0 ? "ready" : processed ? "deleted" : "pending",
                Created = Epoch.AddDays(id),
                CompletedDatetime = round == 0 ? (DateTime?)null : stamp.AddSeconds(-2),
                LastSave = stamp.AddSeconds(processed ? 1 : 0),
                DeleteWhen = round == 0 ? (DateTime?)null : stamp,
                DeletedDatetime = processed ? stamp.AddSeconds(1) : (DateTime?)null,
                Payload = new string((char)('A' + (id + phase) % 26), Sizes[phase % Sizes.Length]),
                Files = Enumerable.Range(0, 1 + phase % 19).ToDictionary(
                    n => "file-" + id + "-" + n, n => Key(id * 10000 + phase * 100 + n))
            };
        }

        internal static Guid Key(int id)
        {
            return new Guid(id, 0, 0, new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
        }

        internal static void Verify(Job actual, Job expected)
        {
            Require(actual != null, "A persisted job is missing.");
            Require(actual.Id == expected.Id && actual.Round == expected.Round && actual.Status == expected.Status,
                "Job identity, round, or status disagrees with the independent ledger.");
            Require(Utc(actual.Created) == expected.Created && Utc(actual.LastSave) == expected.LastSave &&
                Utc(actual.CompletedDatetime) == expected.CompletedDatetime && Utc(actual.DeleteWhen) == expected.DeleteWhen &&
                Utc(actual.DeletedDatetime) == expected.DeletedDatetime, "A persisted timestamp differs from the ledger.");
            Require(actual.Payload == expected.Payload, "The complete persisted payload differs from the ledger.");
            Require(actual.Files != null && actual.Files.Count == expected.Files.Count &&
                expected.Files.All(pair => actual.Files.TryGetValue(pair.Key, out var value) && value == pair.Value),
                "The persisted dictionary differs from the ledger.");
        }

        internal static void VerifyAll(ILiteCollection<Job> collection, int round)
        {
            var rows = collection.FindAll().ToArray();
            Require(rows.Length == Rows && rows.Select(row => row.Id).Distinct().Count() == Rows,
                "The full collection has missing or duplicate IDs.");
            for (var id = 1; id <= Rows; id++)
            {
                var expected = Expected(id, round, round > 0);
                Verify(rows.SingleOrDefault(row => row.Id == expected.Id), expected);
                Verify(collection.FindById(expected.Id), expected);
            }
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        internal static void VerifyRawDates(ILiteCollection<BsonDocument> collection, int round)
        {
            for (var id = 1; id <= Rows; id++)
            {
                var expected = Expected(id, round, round > 0);
                var raw = collection.FindById(expected.Id);
                Require(raw != null, "The raw BSON job is missing.");
                var dates = new Dictionary<string, DateTime?>
                {
                    ["created"] = expected.Created,
                    ["completed_datetime"] = expected.CompletedDatetime,
                    ["last_save"] = expected.LastSave,
                    ["deleted_datetime"] = expected.DeletedDatetime,
                    ["delete_when"] = expected.DeleteWhen
                };
                foreach (var pair in dates)
                {
                    var value = raw[pair.Key];
                    Require(pair.Value.HasValue
                        ? value.IsDateTime && value.AsDateTime.ToUniversalTime() == pair.Value.Value
                        : value.IsNull, "The reported BSON date field name/type/value differs: " + pair.Key);
                }
            }
        }

        private static DateTime? Utc(DateTime? date)
        {
            return date.HasValue ? date.Value.ToUniversalTime() : (DateTime?)null;
        }
    }

    public class Job
    {
        public Guid Id { get; set; }
        public int Round { get; set; }
        public string Status { get; set; }
        [BsonField("created")]
        public DateTime Created { get; set; }
        [BsonField("completed_datetime")]
        public DateTime? CompletedDatetime { get; set; }
        [BsonField("last_save")]
        public DateTime LastSave { get; set; }
        [BsonField("deleted_datetime")]
        public DateTime? DeletedDatetime { get; set; }
        [BsonField("delete_when")]
        public DateTime? DeleteWhen { get; set; }
        public string Payload { get; set; }
        public Dictionary<string, Guid> Files { get; set; }
    }
}
