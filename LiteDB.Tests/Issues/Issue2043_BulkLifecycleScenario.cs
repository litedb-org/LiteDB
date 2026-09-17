using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

namespace LiteDB.Tests.Issues
{
    internal sealed partial class Issue2043_BulkLifecycleScenario
    {
        private const int Batches = 24;
        private const int BatchSize = 32;
        private readonly LiteDatabase _database;
        private readonly LiteDatabase _files;
        private readonly int _seedCount;
        private readonly int _payloadBytes;
        private readonly ConcurrentDictionary<int, byte> _deleted = new ConcurrentDictionary<int, byte>();
        private readonly ConcurrentQueue<Exception> _errors = new ConcurrentQueue<Exception>();
        private readonly ReaderWriterLockSlim _maintenance = new ReaderWriterLockSlim();
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim();
        private readonly CountdownEvent _readersReady = new CountdownEvent(2);
        private int _written;
        private int _cleanupActive;
        private int _cleanupThrough;
        private int _cleanupCommits;
        private int _readerSnapshots;

        public Issue2043_BulkLifecycleScenario(LiteDatabase database, LiteDatabase files, int seedCount, int payloadBytes)
        {
            _database = database;
            _files = files;
            _seedCount = seedCount;
            _payloadBytes = payloadBytes;
        }

        public void Run(string path)
        {
            var records = _database.GetCollection("records", BsonAutoId.Int64);
            records.EnsureIndex("seq", unique: true);
            records.EnsureIndex("bucket");
            InsertBatch(0, _seedCount);
            _database.Checkpoint();
            var seedBytes = new FileInfo(path).Length;
            if (_payloadBytes >= 32768) seedBytes.Should().BeGreaterThan(50L * 1024 * 1024);
            Console.WriteLine($"SEED_2043: rows={_seedCount}, databaseBytes={seedBytes}, firstId=31000");
            VerifyAll(_database);

            var readers = Enumerable.Range(0, 2).Select(_ => Task.Factory.StartNew(
                () => Capture(ReadContinuously), TaskCreationOptions.LongRunning)).ToArray();
            var timer = new Timer(_ => Capture(Cleanup), null, 10, 10);
            var writer = Task.Factory.StartNew(() => Capture(WriteContinuously), TaskCreationOptions.LongRunning);
            try
            {
                writer.Wait(TimeSpan.FromSeconds(90)).Should().BeTrue("the bounded bulk workload must finish");
            }
            finally
            {
                _stop.Set();
                using (var drained = new ManualResetEvent(false))
                {
                    timer.Dispose(drained).Should().BeTrue();
                    drained.WaitOne(TimeSpan.FromSeconds(30)).Should().BeTrue("timer callbacks must finish");
                }
                Task.WaitAll(readers.Concat(new[] { writer }).ToArray(), TimeSpan.FromSeconds(30))
                    .Should().BeTrue("all database users must stop before disposal");
            }
            if (!_errors.IsEmpty) throw new AggregateException(_errors);
            _written.Should().Be(_seedCount + Batches * BatchSize);
            _cleanupCommits.Should().BeGreaterThanOrEqualTo(6);
            _deleted.Count.Should().BeGreaterThan(0);
            _readerSnapshots.Should().BeGreaterThanOrEqualTo(2);
            VerifyAll(_database);
            VerifyFiles(_files);
            _database.Checkpoint();
            _files.Checkpoint();
            Console.WriteLine($"LEDGER_2043: inserted={_written}, deleted={_deleted.Count}, " +
                $"survivors={_written - _deleted.Count}, cleanupCommits={_cleanupCommits}, " +
                $"readerSnapshots={_readerSnapshots}, rebuilds=2, files={Batches}, payloadBytes={_payloadBytes}");
            _maintenance.Dispose();
            _stop.Dispose();
            _readersReady.Dispose();
        }

        private void Capture(Action operation)
        {
            try { operation(); }
            catch (Exception error)
            {
                _errors.Enqueue(error);
                _stop.Set();
            }
        }

        private void WriteContinuously()
        {
            _readersReady.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            for (var batch = 0; batch < Batches && !_stop.IsSet; batch++)
            {
                _maintenance.EnterReadLock();
                try
                {
                    InsertBatch(_written, BatchSize);
                    UploadFile(batch);
                }
                finally { _maintenance.ExitReadLock(); }

                if ((batch + 1) % 4 == 0)
                {
                    SpinWait.SpinUntil(() => Volatile.Read(ref _cleanupThrough) >= _written || _stop.IsSet,
                        TimeSpan.FromSeconds(30)).Should().BeTrue("transactional cleanup must observe every fourth batch");
                }
                if ((batch + 1) % 8 == 0 && batch + 1 < Batches && !_stop.IsSet)
                {
                    // Rebuild closes and reopens the engine. Drain active operations;
                    // the reporter did not establish whether their rebuild was synchronized.
                    _maintenance.EnterWriteLock();
                    try
                    {
                        VerifyAll(_database);
                        _database.Rebuild();
                        VerifyAll(_database);
                    }
                    finally { _maintenance.ExitWriteLock(); }
                }
            }
        }

        private void Cleanup()
        {
            if (_stop.IsSet || Interlocked.Exchange(ref _cleanupActive, 1) != 0) return;
            _maintenance.EnterReadLock();
            try
            {
                var observed = Volatile.Read(ref _written);
                var cutoff = observed - _seedCount / 2;
                var records = _database.GetCollection("records");
                _database.BeginTrans().Should().BeTrue();
                var committed = false;
                try
                {
                    var before = SnapshotSequences(records);
                    records.Count().Should().Be(before.Length);
                    var ordered = records.Query().OrderByDescending("seq").Skip(7).Limit(13).ToArray();
                    VerifyPage(ordered, before.OrderByDescending(seq => seq).Skip(7).Take(13));
                    var victims = records.Find(row => row["seq"] < cutoff, 0, 64).ToArray();
                    victims.Length.Should().Be(Math.Min(64, before.Count(seq => seq < cutoff)),
                        "predicate Find must return every eligible row up to its limit");
                    victims.Select(row => row["seq"].AsInt32).Distinct().Count().Should().Be(victims.Length);
                    foreach (var victim in victims)
                    {
                        var sequence = victim["seq"].AsInt32;
                        sequence.Should().BeLessThan(cutoff);
                        VerifyRow(victim, sequence);
                        records.Delete(31000L + sequence).Should().BeTrue();
                    }
                    (committed = _database.Commit()).Should().BeTrue();
                    foreach (var victim in victims)
                    {
                        _deleted.TryAdd(victim["seq"].AsInt32, 0).Should().BeTrue();
                    }
                    Interlocked.Increment(ref _cleanupCommits);
                    Volatile.Write(ref _cleanupThrough, observed);
                }
                finally { if (!committed) _database.Rollback(); }
            }
            finally
            {
                _maintenance.ExitReadLock();
                Volatile.Write(ref _cleanupActive, 0);
            }
        }

        private void ReadContinuously()
        {
            var first = true;
            while (!_stop.IsSet)
            {
                _maintenance.EnterReadLock();
                try
                {
                    _database.BeginTrans().Should().BeTrue();
                    try
                    {
                        var records = _database.GetCollection("records");
                        var before = SnapshotSequences(records);
                        var page = records.Query().OrderByDescending("seq").Skip(7).Limit(13).ToArray();
                        VerifyPage(page, before.OrderByDescending(seq => seq).Skip(7).Take(13));
                        Interlocked.Increment(ref _readerSnapshots);
                        if (first)
                        {
                            _readersReady.Signal();
                            first = false;
                        }
                    }
                    finally { _database.Rollback(); }
                }
                finally { _maintenance.ExitReadLock(); }
                _stop.Wait(1);
            }
        }
    }
}
