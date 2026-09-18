using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2799_Tests
    {
        [Fact(Skip = "Known bug #2799")]
        public void Rejected_forced_flush_falls_back_and_never_acknowledges_undurable_data()
        {
            var outcome = ExerciseCheckpointAndClose(rejectForcedFlush: true);

            using (new AssertionScope())
            {
                outcome.InsertErrors.Should().BeEmpty();
                outcome.AcknowledgedIds.Should().Equal(1, 2);
                outcome.CheckpointError.Should().BeNull();
                outcome.ReadErrors.Should().BeEmpty();
                outcome.FirstRowVisibleAfterCheckpoint.Should().BeTrue();
                outcome.DisposeError.Should().BeNull();
                outcome.ForcedAfterCheckpoint.Should().BeGreaterOrEqualTo(1,
                    "the fault must intercept the FileStream.Flush(true) path");
                outcome.RejectedAfterCheckpoint.Should().Be(outcome.ForcedAfterCheckpoint);
                outcome.FallbackAfterCheckpoint.Should().BeGreaterOrEqualTo(outcome.RejectedAfterCheckpoint,
                    "each rejected durability flush needs a successful plain flush fallback");
                outcome.ForcedAfterDispose.Should().BeGreaterOrEqualTo(outcome.ForcedAfterCheckpoint);
                outcome.RejectedAfterDispose.Should().Be(outcome.ForcedAfterDispose);
                outcome.FallbackAfterDispose.Should().BeGreaterThan(outcome.FallbackAfterCheckpoint,
                    "disposing with a later committed row must checkpoint it too");
                outcome.WalLengthAfterDispose.Should().Be(0,
                    "a swallowed checkpoint error must not masquerade as a successful close");
                outcome.ReopenError.Should().BeNull();
                outcome.ReopenedRows.Should().Equal("1:checkpoint", "2:dispose");
            }
        }

        [Fact]
        public void Healthy_file_stream_control_uses_the_same_forced_flush_and_persists_both_rows()
        {
            var outcome = ExerciseCheckpointAndClose(rejectForcedFlush: false);

            using (new AssertionScope())
            {
                outcome.InsertErrors.Should().BeEmpty();
                outcome.AcknowledgedIds.Should().Equal(1, 2);
                outcome.CheckpointError.Should().BeNull();
                outcome.ReadErrors.Should().BeEmpty();
                outcome.FirstRowVisibleAfterCheckpoint.Should().BeTrue();
                outcome.DisposeError.Should().BeNull();
                outcome.ForcedAfterCheckpoint.Should().BeGreaterOrEqualTo(1,
                    "the control must prove that the Boolean FileStream overload is exercised");
                outcome.ForcedAfterDispose.Should().BeGreaterThan(outcome.ForcedAfterCheckpoint);
                outcome.RejectedAfterDispose.Should().Be(0);
                outcome.WalLengthAfterDispose.Should().Be(0);
                outcome.ReopenError.Should().BeNull();
                outcome.ReopenedRows.Should().Equal("1:checkpoint", "2:dispose");
            }
        }

        private static ScenarioOutcome ExerciseCheckpointAndClose(bool rejectForcedFlush)
        {
            using var dataFile = new TempFile();
            using var snapshotFile = new TempFile();
            var outcome = new ScenarioOutcome();
            var factory = new InstrumentedFileStreamFactory(dataFile.Filename, rejectForcedFlush);
            LiteDatabase database = null;

            try
            {
                var engine = new LiteEngine(new EngineSettings { Filename = dataFile.Filename });
                database = new LiteDatabase(engine);
                ReplaceDataFactory(engine, factory);
                var collection = database.GetCollection("items");

                TryInsert(collection, 1, "checkpoint", outcome);

                try
                {
                    database.Checkpoint();
                }
                catch (Exception ex)
                {
                    outcome.CheckpointError = ex;
                }

                outcome.ForcedAfterCheckpoint = factory.ForcedFlushCalls;
                outcome.RejectedAfterCheckpoint = factory.RejectedFlushCalls;
                outcome.FallbackAfterCheckpoint = factory.FallbackFlushCalls;

                try
                {
                    outcome.FirstRowVisibleAfterCheckpoint =
                        collection.FindById(1)?["payload"].AsString == "checkpoint";
                }
                catch (Exception ex)
                {
                    outcome.ReadErrors.Add(ex);
                }

                TryInsert(collection, 2, "dispose", outcome);

                try
                {
                    database.Dispose();
                }
                catch (Exception ex)
                {
                    outcome.DisposeError = ex;
                }
                finally
                {
                    database = null;
                }

                outcome.ForcedAfterDispose = factory.ForcedFlushCalls;
                outcome.RejectedAfterDispose = factory.RejectedFlushCalls;
                outcome.FallbackAfterDispose = factory.FallbackFlushCalls;

                var walFilename = FileHelper.GetLogFile(dataFile.Filename);
                outcome.WalLengthAfterDispose = File.Exists(walFilename) ? new FileInfo(walFilename).Length : 0;

                try
                {
                    // Deliberately copy only the data file. Reopening the source could recover from
                    // its WAL and let a swallowed/failed checkpoint appear successful.
                    File.Copy(dataFile.Filename, snapshotFile.Filename, true);
                    using var reopened = new LiteDatabase(snapshotFile.Filename);
                    outcome.ReopenedRows = reopened.GetCollection("items")
                        .FindAll()
                        .OrderBy(x => x["_id"].AsInt32)
                        .Select(x => $"{x["_id"].AsInt32}:{x["payload"].AsString}")
                        .ToArray();
                }
                catch (Exception ex)
                {
                    outcome.ReopenError = ex;
                }
            }
            finally
            {
                database?.Dispose();
                DeleteSidecars(dataFile.Filename);
                DeleteSidecars(snapshotFile.Filename);
            }

            return outcome;
        }

        private static void TryInsert(
            ILiteCollection<BsonDocument> collection, int id, string payload, ScenarioOutcome outcome)
        {
            try
            {
                var acknowledged = collection.Insert(new BsonDocument
                {
                    ["_id"] = id,
                    ["payload"] = payload
                });
                outcome.AcknowledgedIds.Add(acknowledged.AsInt32);
            }
            catch (Exception ex)
            {
                outcome.InsertErrors.Add(ex);
            }
        }

        private static void ReplaceDataFactory(LiteEngine engine, IStreamFactory factory)
        {
            var diskField = GetField(typeof(LiteEngine), "_disk");
            var poolField = GetField(typeof(DiskService), "_dataPool");
            var factoryField = GetField(typeof(DiskService), "_dataFactory");
            var disk = (DiskService)diskField.GetValue(engine);
            var originalPool = (StreamPool)poolField.GetValue(disk);

            originalPool.Dispose();
            factoryField.SetValue(disk, factory);
            poolField.SetValue(disk, new StreamPool(factory, false));
        }

        private static FieldInfo GetField(Type type, string name) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingFieldException(type.FullName, name);

        private static void DeleteSidecars(string filename)
        {
            File.Delete(FileHelper.GetLogFile(filename));
            File.Delete(FileHelper.GetTempFile(filename));
        }

        private sealed class ScenarioOutcome
        {
            public List<Exception> InsertErrors { get; } = new List<Exception>();
            public List<Exception> ReadErrors { get; } = new List<Exception>();
            public List<int> AcknowledgedIds { get; } = new List<int>();
            public Exception CheckpointError { get; set; }
            public Exception DisposeError { get; set; }
            public Exception ReopenError { get; set; }
            public bool FirstRowVisibleAfterCheckpoint { get; set; }
            public int ForcedAfterCheckpoint { get; set; }
            public int RejectedAfterCheckpoint { get; set; }
            public int FallbackAfterCheckpoint { get; set; }
            public int ForcedAfterDispose { get; set; }
            public int RejectedAfterDispose { get; set; }
            public int FallbackAfterDispose { get; set; }
            public long WalLengthAfterDispose { get; set; }
            public string[] ReopenedRows { get; set; } = Array.Empty<string>();
        }

        private sealed class InstrumentedFileStreamFactory : IStreamFactory
        {
            private readonly string _filename;
            private readonly bool _rejectForcedFlush;
            private int _forcedFlushCalls;
            private int _rejectedFlushCalls;
            private int _fallbackFlushCalls;

            public InstrumentedFileStreamFactory(string filename, bool rejectForcedFlush)
            {
                _filename = filename;
                _rejectForcedFlush = rejectForcedFlush;
            }

            public string Name => Path.GetFileName(_filename);
            public bool CloseOnDispose => true;
            public int ForcedFlushCalls => Volatile.Read(ref _forcedFlushCalls);
            public int RejectedFlushCalls => Volatile.Read(ref _rejectedFlushCalls);
            public int FallbackFlushCalls => Volatile.Read(ref _fallbackFlushCalls);

            public Stream GetStream(bool canWrite, bool sequencial)
            {
                var mode = canWrite ? FileMode.OpenOrCreate : FileMode.Open;
                var access = canWrite ? FileAccess.ReadWrite : FileAccess.Read;
                var share = canWrite ? FileShare.Read : FileShare.ReadWrite;
                var options = sequencial ? FileOptions.SequentialScan : FileOptions.RandomAccess;

                return new InstrumentedFileStream(this, _filename, mode, access, share, options);
            }

            public long GetLength() => File.Exists(_filename) ? new FileInfo(_filename).Length : 0;
            public bool Exists() => File.Exists(_filename);
            public void Delete() => File.Delete(_filename);
            public bool IsLocked() => false;
            public void TrimCapacity(Stream stream) { }
            public void Dispose() { }

            private sealed class InstrumentedFileStream : FileStream
            {
                private readonly InstrumentedFileStreamFactory _owner;

                public InstrumentedFileStream(
                    InstrumentedFileStreamFactory owner,
                    string path,
                    FileMode mode,
                    FileAccess access,
                    FileShare share,
                    FileOptions options)
                    : base(path, mode, access, share, Constants.PAGE_SIZE, options)
                {
                    _owner = owner;
                }

                public override void Flush(bool flushToDisk)
                {
                    if (flushToDisk)
                    {
                        Interlocked.Increment(ref _owner._forcedFlushCalls);

                        if (_owner._rejectForcedFlush)
                        {
                            Interlocked.Increment(ref _owner._rejectedFlushCalls);
                            throw new UnauthorizedAccessException("Synthetic network redirector rejection");
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref _owner._fallbackFlushCalls);
                    }

                    base.Flush(flushToDisk);
                }

                public override void Flush()
                {
                    Interlocked.Increment(ref _owner._fallbackFlushCalls);
                    base.Flush();
                }
            }
        }
    }
}
