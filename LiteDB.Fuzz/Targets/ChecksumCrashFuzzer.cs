using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class ChecksumCrashFuzzer : IFuzzTarget
{
    public string Name => "checksum-crash";
    public string Description => "Torn writes and lost volatile bytes during lazy cutover and mixed/complete checkpoints, including repeated recovery.";

    public Task RunAsync(FuzzContext context)
    {
        var events = new Dictionary<string, int>();
        while (context.Next())
        {
            using var fixture = new ChecksumFixture(context);
            var conversion = context.Random.Next(2) == 0;
            var legacy = conversion || context.Random.Next(2) == 0;
            var dirty = context.Random.Next(2) == 0;
            if (dirty || !conversion)
            {
                using var writer = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password);
                writer.CheckpointSize = 0;
                writer.BeginTrans();
                foreach (var id in fixture.Rows.Keys.ToArray())
                {
                    var row = ChecksumFixture.Document(context.Random, id);
                    fixture.Rows[id] = row;
                    writer.GetCollection("rows").Update(ChecksumFixture.Clone(row));
                }
                writer.Commit();
            }
            if (legacy) fixture.MakeLegacy((byte)(8 + context.Random.Next(2)));
            if (!conversion && legacy)
            {
                using var migrate = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password);
                migrate.CheckpointSize = 0;
                var row = ChecksumFixture.Document(context.Random, 200);
                fixture.Rows[200] = row;
                migrate.GetCollection("rows").Insert(ChecksumFixture.Clone(row));
            }
            using var device = new ChecksumCrashDevice(fixture.Data.ToArray(), fixture.Log.ToArray(), context.Random);
            device.Armed = conversion;
            using (var db = ChecksumFixture.Open(device.Data, device.Log, fixture.Password))
            {
                db.CheckpointSize = 0;
                if (!conversion)
                {
                    device.Armed = true;
                    db.Checkpoint();
                }
                device.Armed = false;
            }
            context.Check(device.SelectedData != null, "Crash scenario reached no storage events.");
            var eventKey = (conversion ? "conversion:" : "checkpoint:") + device.SelectedEvent;
            events[eventKey] = events.GetValueOrDefault(eventKey) + 1;
            ChecksumFixture.Save(context, device.SelectedData, device.SelectedLog);
            context.Trace("checksum-crash", new
            {
                conversion, legacy, dirty, encrypted = fixture.Password != null, device.SelectedEvent,
                device.SelectedPrefix, device.SelectedDurable, device.Events
            });
            Verify(context, fixture, device.SelectedData, device.SelectedLog);
            // Crash the repair itself once more. Its exact acknowledged model
            // must remain recoverable, even if the primary header is still torn.
            using var repair = new ChecksumCrashDevice(device.SelectedData, device.SelectedLog, context.Random);
            repair.Armed = true;
            using (var recovered = ChecksumFixture.Open(repair.Data, repair.Log, fixture.Password))
            {
                recovered.CheckpointSize = 0;
                recovered.Checkpoint();
                repair.Armed = false;
            }
            if (repair.SelectedData != null)
            {
                ChecksumFixture.Save(context, repair.SelectedData, repair.SelectedLog);
                Verify(context, fixture, repair.SelectedData, repair.SelectedLog);
            }
            context.ObserveNovelty("checksum-crash", eventKey, device.SelectedDurable, fixture.Password != null);
        }
        context.Metrics["crashEvents"] = events;
        context.Metrics["crashCases"] = context.Steps;
        return Task.CompletedTask;
    }

    private static void Verify(FuzzContext context, ChecksumFixture fixture, byte[] dataBytes, byte[] logBytes)
    {
        using var data = ChecksumFixture.Copy(dataBytes);
        using var log = ChecksumFixture.Copy(logBytes);
        foreach (var readOnly in new[] { true, false, true })
        {
            using (var db = ChecksumFixture.Open(data, log, fixture.Password, readOnly))
            {
                ChecksumFixture.Verify(context, db, fixture.Rows, fixture.Cold);
                if (!readOnly) db.Checkpoint();
            }
            if (readOnly) context.Check(data.ToArray().SequenceEqual(dataBytes) && log.ToArray().SequenceEqual(logBytes),
                "Read-only crash recovery rewrote data or WAL bytes.");
            else
            {
                ChecksumFixture.Audit(context, data, fixture.Password);
                dataBytes = data.ToArray();
                logBytes = log.ToArray();
            }
        }
    }
}
