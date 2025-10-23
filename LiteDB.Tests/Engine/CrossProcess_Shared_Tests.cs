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
        TryDeleteDatabase();
    }

    private void TryDeleteDatabase()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
            var logPath = _dbPath + "-log";
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
            tasks.Add(Task.Run(() => RunChildProcess(processId, documentsPerProcess)));
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
    public async Task CrossProcess_Shared_ConcurrentWrites_MaintainDataIntegrity()
    {
        // This test verifies that concurrent writes from multiple connections maintain data integrity
        // This tests the transactional integrity and locking mechanisms in shared mode
        const int processCount = 5;
        const int operationsPerProcess = 20;

        _output.WriteLine($"Starting concurrent write integrity test with {processCount} tasks");

        // Initialize counter in the database
        using (var db = new LiteDatabase(new ConnectionString
        {
            Filename = _dbPath,
            Connection = ConnectionType.Shared
        }))
        {
            var col = db.GetCollection<BsonDocument>("counter");
            col.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
        }

        // Spawn concurrent tasks that will increment the counter
        var tasks = new List<Task>();
        for (int i = 1; i <= processCount; i++)
        {
            var processId = i;
            tasks.Add(Task.Run(() => RunCounterProcess(processId, operationsPerProcess)));
        }

        await Task.WhenAll(tasks);

        // Verify the final counter value
        using (var db = new LiteDatabase(new ConnectionString
        {
            Filename = _dbPath,
            Connection = ConnectionType.Shared
        }))
        {
            var col = db.GetCollection<BsonDocument>("counter");
            var counter = col.FindById(1);

            var finalValue = counter["value"].AsInt32;
            _output.WriteLine($"Final counter value: {finalValue}");

            // Each task should have incremented the counter
            var expectedValue = processCount * operationsPerProcess;
            finalValue.Should().Be(expectedValue,
                $"Expected counter to be {expectedValue} ({processCount} tasks × {operationsPerProcess} operations each)");
        }

        _output.WriteLine("Concurrent write integrity test completed successfully");
    }

    private void RunChildProcess(int processId, int documentCount)
    {
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

                _output.WriteLine($"Task {processId} completed writing {documentCount} documents");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Task {processId} ERROR: {ex.Message}");
                throw;
            }
        });

        if (!task.Wait(30000)) // 30 second timeout
        {
            throw new TimeoutException($"Task {processId} timed out");
        }

        if (task.IsFaulted)
        {
            throw new Exception($"Task {processId} faulted", task.Exception);
        }
    }

    private void RunCounterProcess(int processId, int operationCount)
    {
        var task = Task.Run(() =>
        {
            try
            {
                _output.WriteLine($"Counter task {processId} starting with {operationCount} operations");

                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = _dbPath,
                    Connection = ConnectionType.Shared
                });

                var col = db.GetCollection<BsonDocument>("counter");

                for (int i = 0; i < operationCount; i++)
                {
                    // Read-modify-write pattern to test transactional integrity
                    var counter = col.FindById(1);
                    var currentValue = counter["value"].AsInt32;
                    counter["value"] = currentValue + 1;
                    col.Update(counter);

                    // Small delay to increase chance of concurrent access
                    Thread.Sleep(5);
                }

                _output.WriteLine($"Counter task {processId} completed {operationCount} operations");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Counter task {processId} ERROR: {ex.Message}");
                throw;
            }
        });

        if (!task.Wait(30000)) // 30 second timeout
        {
            throw new TimeoutException($"Counter task {processId} timed out");
        }

        if (task.IsFaulted)
        {
            throw new Exception($"Counter task {processId} faulted", task.Exception);
        }
    }
}
