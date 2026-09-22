using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine;

public class CrossProcess_Shared_Tests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dbPath;
    private readonly string _testId;
    private readonly List<Task> _workers = new List<Task>();
    private Task _cleanup = Task.CompletedTask;

    public CrossProcess_Shared_Tests(ITestOutputHelper output)
    {
        _output = output;
        _testId = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"litedb_crossprocess_{_testId}.db");

        // Clean up any existing test database
        TryDeleteDatabase();
    }

    public void Dispose()
    {
        // A timed-out worker may still own the files. Report its timeout now,
        // but defer cleanup until every worker actually releases its resources.
        _cleanup = Task.WhenAll(_workers).ContinueWith(completed =>
        {
            _ = completed.Exception; // Observe late worker failures too.
            TryDeleteDatabase();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [Fact]
    public async Task StalledWorker_TimesOutBeforeCompletion_AndDefersFileCleanup()
    {
        var worker = new TaskCompletionSource<bool>();
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(_dbPath, "worker still owns this file");
        var waiting = AwaitWorker(worker.Task, cancellation, "injected worker", 20);
        try
        {
            (await Task.WhenAny(waiting, Task.Delay(1000))).Should().BeSameAs(waiting);
            await Assert.ThrowsAsync<TimeoutException>(() => waiting);
            cancellation.IsCancellationRequested.Should().BeTrue();
            Dispose();
            File.Exists(_dbPath).Should().BeTrue();
        }
        finally { worker.TrySetResult(true); }
        await _cleanup;
        File.Exists(_dbPath).Should().BeFalse();
    }

    private void TryDeleteDatabase()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
            var logPath = FileHelper.GetLogFile(_dbPath);
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task CrossProcess_Shared_MultipleProcesses_CanAccessSameDatabase()
    {
        // This test verifies that multiple concurrent connections can access the same database in shared mode
        // Each Task simulates a separate process/application accessing the database
        const int processCount = 3;
        const int documentsPerProcess = 10;

        _output.WriteLine($"Starting shared mode concurrent access test with {processCount} tasks");
        _output.WriteLine($"Database path: {_dbPath}");

        // Initialize the database in the main process
        using (var db = new LiteDatabase(new ConnectionString
        {
            Filename = _dbPath,
            Connection = ConnectionType.Shared
        }))
        {
            var col = db.GetCollection<BsonDocument>("cross_process_test");
            col.Insert(new BsonDocument { ["_id"] = 0, ["source"] = "main_process", ["timestamp"] = DateTime.UtcNow });
        }

        // Spawn multiple concurrent tasks that will access the database via shared mode
        var tasks = new List<Task>();
        for (int i = 1; i <= processCount; i++)
        {
            var processId = i;
            tasks.Add(RunChildProcess(processId, documentsPerProcess));
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        // Verify all documents were written
        using (var db = new LiteDatabase(new ConnectionString
        {
            Filename = _dbPath,
            Connection = ConnectionType.Shared
        }))
        {
            var col = db.GetCollection<BsonDocument>("cross_process_test");
            var allDocs = col.FindAll().ToList();

            _output.WriteLine($"Total documents found: {allDocs.Count}");

            // Should have 1 (main) + (processCount * documentsPerProcess) documents
            var expectedCount = 1 + (processCount * documentsPerProcess);
            allDocs.Count.Should().Be(expectedCount,
                $"Expected {expectedCount} documents (1 main + {processCount} processes × {documentsPerProcess} docs each)");

            // Verify documents from each concurrent connection
            for (int i = 1; i <= processCount; i++)
            {
                var processSource = $"process_{i}";
                var processDocs = allDocs.Where(d => d["source"].AsString == processSource).ToList();
                processDocs.Count.Should().Be(documentsPerProcess,
                    $"Task {i} should have written {documentsPerProcess} documents");
            }
        }

        _output.WriteLine("Shared mode concurrent access test completed successfully");
    }

    [Fact]
    public async Task CrossProcess_Shared_ConcurrentWrites_InsertDocuments()
    {
        // This test verifies that concurrent inserts from multiple connections work correctly
        // Each task inserts unique documents to test concurrent write capability
        const int taskCount = 5;
        const int documentsPerTask = 20;

        _output.WriteLine($"Starting concurrent insert test with {taskCount} tasks");

        // Initialize collection
        using (var db = new LiteDatabase(new ConnectionString
        {
            Filename = _dbPath,
            Connection = ConnectionType.Shared
        }))
        {
            var col = db.GetCollection<BsonDocument>("concurrent_inserts");
            col.EnsureIndex("task_id");
        }

        // Spawn concurrent tasks that will insert documents
        var tasks = new List<Task>();
        for (int i = 1; i <= taskCount; i++)
        {
            var taskId = i;
            tasks.Add(RunInsertTask(taskId, documentsPerTask));
        }

        await Task.WhenAll(tasks);

        // Verify all documents were inserted
        using (var db = new LiteDatabase(new ConnectionString
        {
            Filename = _dbPath,
            Connection = ConnectionType.Shared
        }))
        {
            var col = db.GetCollection<BsonDocument>("concurrent_inserts");
            var totalDocs = col.Count();

            var expectedCount = taskCount * documentsPerTask;
            totalDocs.Should().Be(expectedCount,
                $"Expected {expectedCount} documents ({taskCount} tasks × {documentsPerTask} docs each)");

            // Verify each task inserted the correct number
            for (int i = 1; i <= taskCount; i++)
            {
                var taskDocs = col.Count(Query.EQ("task_id", i));
                taskDocs.Should().Be(documentsPerTask,
                    $"Task {i} should have inserted {documentsPerTask} documents");
            }
        }

        _output.WriteLine("Concurrent insert test completed successfully");
    }

    private async Task RunInsertTask(int taskId, int documentCount)
    {
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var elapsed = Stopwatch.StartNew();
        var task = Task.Run(() =>
        {
            try
            {
                _output.WriteLine($"Insert task {taskId} starting with {documentCount} documents after {elapsed.ElapsedMilliseconds} ms");

                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = _dbPath,
                    Connection = ConnectionType.Shared
                });

                var col = db.GetCollection<BsonDocument>("concurrent_inserts");

                for (int i = 0; i < documentCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var doc = new BsonDocument
                    {
                        ["task_id"] = taskId,
                        ["doc_number"] = i,
                        ["timestamp"] = DateTime.UtcNow,
                        ["data"] = $"Data from task {taskId}, document {i}"
                    };

                    col.Insert(doc);

                    // Small delay to ensure concurrent access
                    Thread.Sleep(2);
                }

                _output.WriteLine($"Insert task {taskId} completed {documentCount} insertions after {elapsed.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Insert task {taskId} ERROR: {ex.Message}");
                throw;
            }
        });

        await AwaitWorker(task, cancellation, $"Insert task {taskId}");
    }

    private async Task RunChildProcess(int processId, int documentCount)
    {
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var elapsed = Stopwatch.StartNew();
        // Instead of spawning actual processes, we'll use Tasks to simulate concurrent access
        // This is safer for CI environments and still tests the shared mode locking
        var task = Task.Run(() =>
        {
            try
            {
                _output.WriteLine($"Task {processId} starting with {documentCount} documents to write");

                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = _dbPath,
                    Connection = ConnectionType.Shared
                });

                var col = db.GetCollection<BsonDocument>("cross_process_test");

                for (int i = 0; i < documentCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var doc = new BsonDocument
                    {
                        ["source"] = $"process_{processId}",
                        ["doc_number"] = i,
                        ["timestamp"] = DateTime.UtcNow,
                        ["thread_id"] = Thread.CurrentThread.ManagedThreadId
                    };

                    col.Insert(doc);

                    // Small delay to ensure concurrent access
                    Thread.Sleep(10);
                }

                _output.WriteLine($"Task {processId} completed writing {documentCount} documents after {elapsed.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Task {processId} ERROR: {ex.Message}");
                throw;
            }
        });

        await AwaitWorker(task, cancellation, $"Task {processId}");
    }

    private async Task AwaitWorker(Task task, CancellationTokenSource cancellation, string name, int timeoutMilliseconds = 30000)
    {
        _workers.Add(task);
        if (await Task.WhenAny(task, Task.Delay(timeoutMilliseconds)) != task)
        {
            _output.WriteLine($"{name} exceeded its {timeoutMilliseconds}-ms deadline; cancelling remaining inserts");
            cancellation.Cancel();
            throw new TimeoutException($"{name} timed out");
        }
        await task;
    }
}
