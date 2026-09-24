using LiteDB.Tests.Mapper;

namespace LiteDB.Fuzz.Targets;

internal sealed class WalFuzzer : IFuzzTarget
{
    public string Name => "wal";
    public string Description => "Systematic stream-failure recovery across multi-transaction, rollback, and checkpoint workloads.";

    public Task RunAsync(FuzzContext context)
    {
        var baselineData = new MemoryStream();
        var baselineLog = new MemoryStream();
        var baseline = new SortedDictionary<int, BsonDocument>();
        using (var seed = new LiteDatabase(baselineData, logStream: baselineLog))
        {
            var rows = seed.GetCollection("rows");
            for (var id = 1; id <= 24; id++)
            {
                var document = Document(new StableRandom(id), id, 200 + id * 17);
                rows.Insert(Clone(document));
                baseline[id] = document;
            }
            rows.EnsureIndex("value", "Value");
            seed.Checkpoint();
        }

        var dataBytes = baselineData.ToArray();
        var logBytes = baselineLog.ToArray();
        var injected = 0;
        var acknowledged = 0;
        var integrityChecks = 0;
        var firedEvents = new Dictionary<string, int>(StringComparer.Ordinal);
        while (context.Next())
        {
            var scenario = (context.Steps - 1) / 192;
            var random = new StableRandom(unchecked(context.Seed + scenario * 104729));
            var schedule = new FailureSchedule
            {
                FailAt = (context.Steps - 1) % 192 + 1,
                PartialWrite = ((context.Steps + scenario) & 1) == 0
            };
            using var data = new EventStream(dataBytes, schedule, "data");
            using var log = new EventStream(logBytes, schedule, "log");
            var expected = CloneModel(baseline);
            SortedDictionary<int, BsonDocument> possibleCommit = null;
            var commitEntered = false;
            Exception failure = null;
            LiteDatabase db = null;
            try
            {
                db = new LiteDatabase(data, logStream: log);
                schedule.Enabled = true;
                var rows = db.GetCollection("rows");
                var batches = 2 + random.Next(4);
                for (var batch = 0; batch < batches; batch++)
                {
                    db.BeginTrans();
                    var candidate = CloneModel(expected);
                    for (var operation = 0; operation < 3 + random.Next(9); operation++)
                        Mutate(rows, candidate, random, batch, operation);
                    possibleCommit = candidate;
                    commitEntered = true;
                    db.Commit();
                    expected = candidate;
                    possibleCommit = null;
                    commitEntered = false;
                    acknowledged++;
                    if ((batch + scenario) % 3 == 0) db.Checkpoint();
                }

                db.BeginTrans();
                var rolledBack = CloneModel(expected);
                for (var operation = 0; operation < 5; operation++) Mutate(rows, rolledBack, random, 99, operation);
                db.Rollback();
                if ((scenario & 1) == 0) db.Checkpoint();
            }
            catch (Exception error) when (schedule.Fired)
            {
                failure = error;
                injected++;
            }
            finally
            {
                schedule.Enabled = false;
                try { db?.Dispose(); }
                catch when (schedule.Fired) { }
            }

            if (schedule.FiredEvent != null)
            {
                var category = schedule.FiredEvent[(schedule.FiredEvent.IndexOf('.') + 1)..].Split('(')[0];
                firedEvents[category] = firedEvents.GetValueOrDefault(category) + 1;
                context.ObserveNovelty("wal-failure", category, schedule.PartialWrite, scenario % 8);
            }
            context.Trace("failure-schedule", new
            {
                scenario, schedule.FailAt, schedule.PartialWrite, schedule.Fired,
                schedule.FiredEvent, error = failure?.GetType().FullName, events = schedule.EventCount,
                documents = expected.Count
            });
            VerifyRecovery(context, data.ToArray(), log.ToArray(), expected,
                commitEntered ? possibleCommit : null, schedule, ref integrityChecks);
        }
        if (context.Steps >= 192)
        {
            foreach (var required in new[] { "Read", "Write", "Flush", "SetLength" })
                context.Check(firedEvents.GetValueOrDefault(required) > 0, $"WAL schedule missed {required} failures.");
            context.Check(injected > 0 && integrityChecks > 0,
                "WAL campaign missed injected failures or recovered-file integrity walks.");
        }
        context.Metrics["injectedFailures"] = injected;
        context.Metrics["acknowledgedCommits"] = acknowledged;
        context.Metrics["failureEvents"] = firedEvents;
        context.Metrics["integrityChecks"] = integrityChecks;
        return Task.CompletedTask;
    }

    private static void Mutate(ILiteCollection<BsonDocument> rows, SortedDictionary<int, BsonDocument> model,
        Random random, int batch, int operation)
    {
        var id = random.Next(1, 70);
        if ((batch + operation + random.Next(4)) % 5 == 0)
        {
            rows.Delete(id);
            model.Remove(id);
            return;
        }
        var size = random.Next(5) == 0 ? random.Next(9000, 24000) : random.Next(0, 900);
        var document = Document(random, id, size);
        rows.Upsert(Clone(document));
        model[id] = document;
    }

    private static void VerifyRecovery(FuzzContext context, byte[] dataBytes, byte[] logBytes,
        SortedDictionary<int, BsonDocument> expected, SortedDictionary<int, BsonDocument> possibleCommit,
        FailureSchedule schedule, ref int integrityChecks)
    {
        using var data = Initialized(dataBytes);
        using var log = Initialized(logBytes);
        try
        {
            using var recovered = new LiteDatabase(data, logStream: log);
            var actual = recovered.GetCollection("rows").Query().OrderBy("_id").ToArray();
            var exact = Matches(actual, expected);
            var uncertainCommit = possibleCommit != null && Matches(actual, possibleCommit);
            context.Check(exact || uncertainCommit,
                $"Recovery produced a partial or unknown state after {schedule.FiredEvent}.");
            var byValue = recovered.GetCollection("rows").Query().OrderBy("Value").ThenBy("_id").ToArray();
            var chosen = exact ? expected : possibleCommit;
            var ordered = chosen.Values.OrderBy(value => value["Value"]).ThenBy(value => value["_id"]).ToArray();
            context.Check(byValue.Select(value => value["_id"]).SequenceEqual(ordered.Select(value => value["_id"])),
                "Recovered secondary-index order differs from the recovered model.");
            if (context.Steps % 31 == 0)
            {
                recovered.Checkpoint();
                var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "wal-recovered.db"));
                File.WriteAllBytes(file, data.ToArray());
                DatabaseIntegrityVerifier.Verify(context, file);
                integrityChecks++;
            }
        }
        catch (FuzzFailureException) { throw; }
        catch (Exception error)
        {
            throw new FuzzFailureException($"Recovery rejected event {schedule.FiredEvent} " +
                $"(partial={schedule.PartialWrite}): {error}");
        }
    }

    private static bool Matches(BsonDocument[] actual, SortedDictionary<int, BsonDocument> expected) =>
        actual.Length == expected.Count && actual.Zip(expected.Values,
            (left, right) => BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right))).All(value => value);

    private static BsonDocument Document(Random random, int id, int size) => new()
    {
        ["_id"] = id,
        ["Value"] = random.Next(-10000, 10001),
        ["Version"] = random.Next(),
        ["Payload"] = new string((char)('a' + random.Next(26)), size),
        ["Nested"] = new BsonDocument { ["Batch"] = random.Next(), ["Flag"] = random.Next(2) == 0 }
    };

    private static BsonDocument Clone(BsonDocument document) =>
        BsonSerializer.Deserialize(BsonSerializer.Serialize(document));

    private static SortedDictionary<int, BsonDocument> CloneModel(SortedDictionary<int, BsonDocument> source) =>
        new(source.ToDictionary(pair => pair.Key, pair => Clone(pair.Value)));

    private static MemoryStream Initialized(byte[] bytes)
    {
        var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;
        return stream;
    }

    private sealed class FailureSchedule
    {
        internal bool Enabled;
        internal int FailAt;
        internal bool PartialWrite;
        internal int EventCount;
        internal bool Fired;
        internal string FiredEvent;

        internal bool Visit(string name)
        {
            if (!Enabled || Fired) return false;
            EventCount++;
            if (EventCount != FailAt) return false;
            Fired = true;
            FiredEvent = name;
            return true;
        }
    }

    private sealed class EventStream : MemoryStream
    {
        private readonly FailureSchedule _schedule;
        private readonly string _name;

        internal EventStream(byte[] initial, FailureSchedule schedule, string name)
        {
            _schedule = schedule;
            _name = name;
            base.Write(initial, 0, initial.Length);
            Position = 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_schedule.Visit($"{_name}.Read({Position},{count})")) throw Failure();
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (_schedule.Visit($"{_name}.Read({Position},{buffer.Length})")) throw Failure();
            return base.Read(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_schedule.Visit($"{_name}.Write({Position},{count})")) { base.Write(buffer, offset, count); return; }
            if (_schedule.PartialWrite && count > 1) base.Write(buffer, offset, count / 2);
            throw Failure();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_schedule.Visit($"{_name}.Write({Position},{buffer.Length})")) { base.Write(buffer); return; }
            if (_schedule.PartialWrite && buffer.Length > 1) base.Write(buffer[..(buffer.Length / 2)]);
            throw Failure();
        }

        public override void SetLength(long value)
        {
            if (_schedule.Visit($"{_name}.SetLength({value})")) throw Failure();
            base.SetLength(value);
        }

        public override void Flush()
        {
            if (_schedule.Visit($"{_name}.Flush")) throw Failure();
            base.Flush();
        }

        private IOException Failure() => new($"Injected failure at {_schedule.FiredEvent}");
    }
}
