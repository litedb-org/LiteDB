using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2059_Tests
    {
        private static readonly string[] ReportedInvoiceNumbers =
        {
            "xp148jm576/43500001/2021/dk499zp719",
            "xp148jm576/43500011/2021/dk499zp719",
            "xp148jm576/943500037/2021/dk499zp719"
        };

        public class SimpleRecord
        {
            public int Id { get; set; }
            public string InvNum { get; set; }
            public bool Zastareo { get; set; }
            public bool CisError { get; set; }
            public DateTime SendDateTime { get; set; }
            public string Payload { get; set; }
        }

        [Fact]
        public void Reported_indexed_lookup_matches_external_ledger_after_variable_size_churn_and_reopens()
        {
            VerifyLookupLifecycle(480, 1);
        }

        internal void VerifyLookupLifecycle(int decoyCount, int payloadScale, Action<string> probeBackupLock = null)
        {
            using var file = new TempFile();
            var ledger = new Dictionary<int, SimpleRecord>();
            var nextId = 1;

            using (var db = new LiteDatabase(file.Filename))
            {
                var records = db.GetCollection<SimpleRecord>("SimpleRecord");
                records.EnsureIndex(x => x.InvNum).Should().BeTrue();

                foreach (var invoice in ReportedInvoiceNumbers)
                {
                    TrackInsert(records, ledger, NewRecord(nextId++, invoice, false, false, 1, 180));
                    TrackInsert(records, ledger, NewRecord(nextId++, invoice, false, false, 3, 640));
                    TrackInsert(records, ledger, NewRecord(nextId++, invoice, true, false, 5, 280));
                    TrackInsert(records, ledger, NewRecord(nextId++, invoice, false, true, 7, 760));
                }

                for (var i = 0; i < decoyCount; i++)
                {
                    TrackInsert(records, ledger, NewRecord(
                        nextId++, "decoy/" + (i % 37) + "/" + i, i % 11 == 0, i % 17 == 0,
                        i % 29, (80 + (i * 53 % 880)) * payloadScale));
                }

                db.Checkpoint();
            }
            var seededBytes = new FileInfo(file.Filename).Length;
            if (payloadScale > 1) seededBytes.Should().BeGreaterThan(15L * 1024 * 1024);
            Console.WriteLine($"SEED_2059: records={ledger.Count}, databaseBytes={seededBytes}");

            for (var round = 0; round < 4; round++)
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    var records = db.GetCollection<SimpleRecord>("SimpleRecord");
                    var changed = ledger.Values.Where(x => x.Id > 12)
                        .OrderBy(x => x.Id).Skip(round * 31).Take(24).ToArray();

                    foreach (var old in changed)
                    {
                        var updated = Copy(old);
                        updated.Payload = MakePayload(old.Id, (round % 2 == 0 ? 1050 : 45) * payloadScale);
                        records.Update(updated).Should().BeTrue();
                        ledger[updated.Id] = Copy(updated);
                    }

                    var removed = ledger.Keys.Where(x => x > 12 && x % 23 == round + 2)
                        .OrderBy(x => x).Take(10).ToArray();
                    foreach (var id in removed)
                    {
                        records.Delete(id).Should().BeTrue();
                        ledger.Remove(id).Should().BeTrue();
                    }

                    for (var i = 0; i < 36; i++)
                    {
                        TrackInsert(records, ledger, NewRecord(
                            nextId++, "round/" + round + "/" + (i % 9), false, false,
                            round * 40 + i, (120 + (i * 97 % 900)) * payloadScale));
                    }

                    AssertLedgerAndReportedQueries(records, ledger);
                    db.Checkpoint();
                }

                using var reopened = new LiteDatabase(file.Filename);
                AssertLedgerAndReportedQueries(
                    reopened.GetCollection<SimpleRecord>("SimpleRecord"), ledger);
            }

            var beforeReadOnlyWorkload = File.ReadAllBytes(file.Filename);
            foreach (var invoice in ReportedInvoiceNumbers)
            {
                using var db = new LiteDatabase(file.Filename);
                var records = db.GetCollection<SimpleRecord>("SimpleRecord");
                var query = records.Query()
                    .Where(x => x.InvNum == invoice && x.Zastareo == false && x.CisError == false);
                query.GetPlan()["index"]["expr"].AsString.Should().Be("$.InvNum");

                var matches = records.Query()
                    .Where(x => x.InvNum == invoice && x.Zastareo == false && x.CisError == false)
                    .ToList();
                var newest = matches.OrderByDescending(x => x.SendDateTime).DefaultIfEmpty().First();
                var expected = ledger.Values.Where(x => x.InvNum == invoice && !x.Zastareo && !x.CisError)
                    .OrderByDescending(x => x.SendDateTime).First();
                Fingerprint(newest).Should().Be(Fingerprint(expected));
            }

            using (new AssertionScope())
            {
                File.ReadAllBytes(file.Filename).Should().Equal(beforeReadOnlyWorkload,
                    "successful lookups must not hide corruption by rewriting or dropping data");
                beforeReadOnlyWorkload.Length.Should().BeGreaterThan(32 * 8192,
                    "the control must span many pages rather than only prove a tiny happy path");
                (beforeReadOnlyWorkload.Length % 8192).Should().Be(0,
                    "a healthy LiteDB data file consists of complete pages");
            }

            // The historical runner supplies a real process holding a backup lock.
            // Only normal file sharing is exercised; no database bytes are altered.
            probeBackupLock?.Invoke(file.Filename);
            using (var recovered = new LiteDatabase(file.Filename))
            {
                var records = recovered.GetCollection<SimpleRecord>("SimpleRecord");
                AssertLedgerAndReportedQueries(records, ledger);
                TrackInsert(records, ledger, NewRecord(nextId++, ReportedInvoiceNumbers[0],
                    false, false, 200, 1100 * payloadScale));
                recovered.Checkpoint();
            }
            var beforeFinalReopen = File.ReadAllBytes(file.Filename);
            using (var final = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
            {
                AssertLedgerAndReportedQueries(final.GetCollection<SimpleRecord>("SimpleRecord"), ledger);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(beforeFinalReopen);
            Console.WriteLine($"LEDGER_2059: records={ledger.Count}, recoveryId={nextId - 1}, " +
                "rounds=4, indexedQueries=3, recoveryWrite=true, readOnlyBytesUnchanged=true");
        }

        private static void AssertLedgerAndReportedQueries(
            ILiteCollection<SimpleRecord> records,
            IReadOnlyDictionary<int, SimpleRecord> ledger)
        {
            records.Count().Should().Be(ledger.Count);
            records.FindAll().Select(Fingerprint).OrderBy(x => x)
                .Should().Equal(ledger.Values.Select(Fingerprint).OrderBy(x => x));

            foreach (var invoice in ReportedInvoiceNumbers)
            {
                var expected = ledger.Values
                    .Where(x => x.InvNum == invoice && !x.Zastareo && !x.CisError)
                    .Select(Fingerprint).OrderBy(x => x).ToArray();
                var actual = records.Query()
                    .Where(x => x.InvNum == invoice && x.Zastareo == false && x.CisError == false)
                    .ToList().Select(Fingerprint).OrderBy(x => x).ToArray();
                actual.Should().Equal(expected);
            }
        }

        private static void TrackInsert(
            ILiteCollection<SimpleRecord> records,
            IDictionary<int, SimpleRecord> ledger,
            SimpleRecord record)
        {
            records.Insert(record).AsInt32.Should().Be(record.Id);
            ledger.Add(record.Id, Copy(record));
        }

        private static SimpleRecord NewRecord(
            int id, string invoice, bool stale, bool error, int dayOffset, int payloadLength)
        {
            return new SimpleRecord
            {
                Id = id,
                InvNum = invoice,
                Zastareo = stale,
                CisError = error,
                SendDateTime = new DateTime(2021, 8, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(dayOffset),
                Payload = MakePayload(id, payloadLength)
            };
        }

        private static SimpleRecord Copy(SimpleRecord record)
        {
            return new SimpleRecord
            {
                Id = record.Id,
                InvNum = record.InvNum,
                Zastareo = record.Zastareo,
                CisError = record.CisError,
                SendDateTime = record.SendDateTime,
                Payload = record.Payload
            };
        }

        private static string MakePayload(int seed, int length)
        {
            return new string((char)('a' + seed % 26), length);
        }

        private static string Fingerprint(SimpleRecord record)
        {
            return string.Join("|", record.Id, record.InvNum, record.Zastareo,
                record.CisError, record.SendDateTime.ToUniversalTime().Ticks, record.Payload);
        }
    }
}
