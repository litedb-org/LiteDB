using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_TornWalAppend;

/// <summary>
/// Defect fixed by PR #3027 (guards: TornWalAppend_Tests, TornSlotRewrite_Tests). WAL frames carry a
/// CRC and recovery stops at the first frame whose checksum fails, discarding everything behind it. A
/// WAL append that fails part-way leaves a torn frame in the WAL. The known-bad engine only rolled the
/// failed transaction back when the failure was not an IOException (an exception from a caller's log
/// stream, UnauthorizedAccessException for EACCES) and kept committing: when the truncation of the
/// torn frame failed too, or when a buffering caller log stream (a BufferedStream) held the frame and
/// tore it only when it wrote it on later, the next commits were acknowledged behind the torn frame
/// and lost at the next recovery. Fixed: such a failure stops the
/// engine and is recorded, the engine continues read-only (decision 6 of
/// docs/decisions/durability-policy.md) and refuses every later write before it changes a byte, so no
/// commit is acknowledged behind a torn frame, and recovery keeps every acknowledged commit.
///
/// Black box: the database runs on caller streams (EngineSettings.DataStream/LogStream). The log
/// stream is a MemoryStream whose armed frame write stores half the frame and throws, and whose
/// truncation then fails too; in the buffered scenario it sits under a 64 KiB BufferedStream, which
/// writes the frame on to it only later. TransactionPageLimit = 1 makes the insert write its pages to
/// the WAL at safepoints before its commit. The streams' bytes are what a killed process leaves.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    // A WAL frame: a page (8192 bytes) and its 64-byte checksum trailer.
    private const int FrameSize = 8192 + 64;
    private const string Refusal = "Cannot modify this database: an earlier write failed, so the engine continues read-only";
    private const string TornFrame = "injected torn frame write";

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host);
            host.SendResult(code == Reproduced, summary);
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, $"INCONCLUSIVE: {error.GetType().Name}: {error.Message}", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return Inconclusive;
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host)
    {
        var scenarios = new[]
        {
            Scenario(host, "torn append whose truncation failed", buffered: false),
            Scenario(host, "frame a BufferedStream log tore later", buffered: true),
        };

        var lost = scenarios.Where(x => x.Acknowledged.Except(x.Recovered).Any()).ToArray();
        if (lost.Length > 0)
        {
            foreach (var scenario in scenarios)
                Require(!scenario.Recovered.Except(scenario.Acknowledged).Any(),
                    $"{scenario.Name}: recovered {Ids(scenario.Recovered)}, more than the acknowledged {Ids(scenario.Acknowledged)}");
            return (Reproduced, "REPRODUCED: commits acknowledged behind a torn WAL frame were lost at recovery (" +
                string.Join("; ", lost.Select(x => $"{x.Name}: acknowledged [{Ids(x.Acknowledged)}], recovered [{Ids(x.Recovered)}]")) + ")");
        }

        foreach (var scenario in scenarios)
        {
            Require(scenario.Refused, $"{scenario.Name}: the writes after the failure were not all refused with the recorded failure");
            Require(scenario.Recovered.SequenceEqual(Enumerable.Range(1, 20)), $"{scenario.Name}: recovered {Ids(scenario.Recovered)}, expected 1-20");
            Require(scenario.Later, $"{scenario.Name}: the recovered database did not take and keep later commits");
        }
        return (Fixed, "FIXED: after a torn WAL frame (its truncation failed, or a BufferedStream log tore it later) the engine continued " +
            "read-only and refused every later write; recovery kept every acknowledged commit and took new ones");
    }

    private sealed record Outcome(string Name, List<int> Acknowledged, List<int> Recovered, bool Refused, bool Later);

    private static Outcome Scenario(ReproHostClient host, string name, bool buffered)
    {
        using var data = new MemoryStream();
        using var device = new TornLog();
        var acknowledged = Enumerable.Range(1, 20).ToList();
        var refused = false;
        (byte[] Data, byte[] Log) image;
        using (var log = buffered ? new BufferedStream(device, 65536) : (Stream)device)
        using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 1 }))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(acknowledged.Select(id => Row(id, 0)));

            // Direct: the third frame the insert writes (on the known-bad engine the next commit writes over a
            // torn first or second frame). Buffered: the first frame the BufferedStream writes on.
            device.Arm(buffered ? 1 : 3, failSetLength: !buffered);
            try
            {
                rows.Insert(Enumerable.Range(100, 30).Select(id => Row(id, 0)));
                throw new InvalidOperationException($"{name}: the insert whose frame was torn succeeded");
            }
            catch (Exception error) when (error.ToString().Contains(TornFrame) || error.ToString().Contains("injected truncation failure"))
            {
                host.SendLog($"{name}: the insert whose frame was torn threw {error.GetType().Name}: {error.Message}");
            }
            Require(device.Torn, $"{name}: no frame was torn");
            Require(buffered || device.SetLengthFailed, $"{name}: the truncation of the torn frame did not fail");
            device.Disarm();

            var files = (Data: data.ToArray(), Log: device.ToArray());
            var refusals = 0;
            foreach (var id in new[] { 200, 201 })
            {
                try
                {
                    rows.Insert(Row(id, 0));
                    acknowledged.Add(id);
                    host.SendLog($"{name}: the insert of _id {id} after the failure was acknowledged");
                }
                catch (IOException error) when (error.Message.StartsWith(Refusal, StringComparison.Ordinal) && error.ToString().Contains(TornFrame))
                {
                    refusals++;
                    host.SendLog($"{name}: the insert of _id {id} after the failure was refused: {error.Message}");
                }
            }

            if (refusals == 2)
            {
                // The fixed engine continues read-only: it reads what the streams hold, refuses an update
                // too, and writes nothing, so nothing lands behind the torn frame.
                var read = rows.FindAll().Select(x => x["_id"].AsInt32).ToList();
                Require(read.SequenceEqual(Enumerable.Range(1, 20)), $"{name}: the read-only engine reads {Ids(read)}, expected 1-20");
                try
                {
                    rows.Update(Row(1, 7));
                    throw new InvalidOperationException($"{name}: an update after the failure was not refused");
                }
                catch (IOException error) when (error.Message.StartsWith(Refusal, StringComparison.Ordinal)) { }
                Require(rows.Count() == 20, $"{name}: the read-only engine counts {rows.Count()} rows");
                Require(data.ToArray().SequenceEqual(files.Data) && device.ToArray().SequenceEqual(files.Log),
                    $"{name}: the read-only engine changed the streams");
                refused = true;
            }
            // A killed process leaves the bytes that reached the device.
            image = (data.ToArray(), device.ToArray());
        }

        var (recovered, later) = Recover(image);
        host.SendLog($"{name}: acknowledged [{Ids(acknowledged)}], recovered [{Ids(recovered)}]");
        return new Outcome(name, acknowledged, recovered, refused, later);
    }

    /// <summary>The rows recovered from the image, and whether the recovered database takes and keeps new commits.</summary>
    private static (List<int> Ids, bool Later) Recover((byte[] Data, byte[] Log) image)
    {
        using var data = new MemoryStream();
        using var log = new MemoryStream();
        data.Write(image.Data);
        log.Write(image.Log);
        List<int> ids;
        using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            ids = rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToList();
            if (!ids.SequenceEqual(Enumerable.Range(1, 20))) return (ids, false);
            Require(rows.Update(Enumerable.Range(1, 20).Select(id => Row(id, 7))) == 20, "the recovered database did not update 20 rows");
            rows.Insert(Row(300, 0));
        }
        using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = new MemoryStream(data.ToArray()), LogStream = new MemoryStream(log.ToArray()) })))
        {
            var docs = db.GetCollection("rows").FindAll().ToList();
            Require(docs.Select(x => x["_id"].AsInt32).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, 20).Append(300)) &&
                docs.Where(x => x["_id"].AsInt32 <= 20).All(x => x["value"].AsInt32 == 7),
                $"after later commits the recovered database holds {Ids(docs.Select(x => x["_id"].AsInt32))}");
        }
        return (ids, true);
    }

    private static string Ids(IEnumerable<int> ids)
    {
        var sorted = ids.OrderBy(x => x).ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j - i >= 2 ? $"{sorted[i]}-{sorted[j]}" : string.Join(", ", sorted.Skip(i).Take(j - i + 1)));
            i = j + 1;
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static BsonDocument Row(int id, int value) => new BsonDocument
    {
        ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 500)
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// A log stream whose n-th frame write after arming stores half the frame and then throws (not an
    /// IOException: a caller stream's own failure), and whose truncation then fails too when armed so.
    /// </summary>
    private sealed class TornLog : MemoryStream
    {
        private int _tearFrame;
        private bool _failSetLength;
        internal bool Torn, SetLengthFailed;

        internal void Arm(int frame, bool failSetLength)
        {
            _tearFrame = frame;
            _failSetLength = failSetLength;
        }

        internal void Disarm() => _tearFrame = 0;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_tearFrame > 0 && count == FrameSize && --_tearFrame == 0)
            {
                base.Write(buffer, offset, count / 2);
                Torn = true;
                throw new InvalidOperationException(TornFrame);
            }
            base.Write(buffer, offset, count);
        }

        public override void SetLength(long value)
        {
            if (_failSetLength && Torn && !SetLengthFailed)
            {
                SetLengthFailed = true;
                throw new InvalidOperationException("injected truncation failure");
            }
            base.SetLength(value);
        }
    }
}
