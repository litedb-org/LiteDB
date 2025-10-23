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
        // This test verifies that multiple processes can access the same database in shared mode
        const int processCount = 3;
        const int documentsPerProcess = 10;

        _output.WriteLine($"Starting cross-process test with {processCount} processes");
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

        // Spawn multiple processes that will access the database
        var tasks = new List<Task>();
        for (int i = 1; i <= processCount; i++)
        {
            var processId = i;
            tasks.Add(Task.Run(() => RunChildProcess(processId, documentsPerProcess)));
        }

        // Wait for all child processes to complete
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

            // Verify documents from each process
            for (int i = 1; i <= processCount; i++)
            {
                var processSource = $"process_{i}";
                var processDocs = allDocs.Where(d => d["source"].AsString == processSource).ToList();
                processDocs.Count.Should().Be(documentsPerProcess,
                    $"Process {i} should have written {documentsPerProcess} documents");
            }
        }

        _output.WriteLine("Cross-process test completed successfully");
    }

    [Fact]
    public async Task CrossProcess_Shared_ConcurrentWrites_MaintainDataIntegrity()
    {
        // This test verifies that concurrent writes from multiple processes maintain data integrity
        const int processCount = 5;
        const int operationsPerProcess = 20;

        _output.WriteLine($"Starting concurrent write test with {processCount} processes");

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

        // Spawn processes that will increment the counter
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

            // Each process should have incremented the counter
            var expectedValue = processCount * operationsPerProcess;
            finalValue.Should().Be(expectedValue,
                $"Expected counter to be {expectedValue} ({processCount} processes × {operationsPerProcess} operations each)");
        }

        _output.WriteLine("Concurrent write test completed successfully");
    }

    private void RunChildProcess(int processId, int documentCount)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{GetTestAssemblyPath()}\" --crossprocess-worker --db \"{_dbPath}\" --process-id {processId} --doc-count {documentCount}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _output.WriteLine($"[Process {processId}] {e.Data}");
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _output.WriteLine($"[Process {processId} ERROR] {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(30000)) // 30 second timeout
        {
            process.Kill();
            throw new TimeoutException($"Process {processId} timed out");
        }

        if (process.ExitCode != 0)
        {
            throw new Exception($"Process {processId} exited with code {process.ExitCode}");
        }
    }

    private void RunCounterProcess(int processId, int operationCount)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{GetTestAssemblyPath()}\" --crossprocess-counter --db \"{_dbPath}\" --process-id {processId} --operation-count {operationCount}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _output.WriteLine($"[Counter {processId}] {e.Data}");
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _output.WriteLine($"[Counter {processId} ERROR] {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(30000)) // 30 second timeout
        {
            process.Kill();
            throw new TimeoutException($"Counter process {processId} timed out");
        }

        if (process.ExitCode != 0)
        {
            throw new Exception($"Counter process {processId} exited with code {process.ExitCode}");
        }
    }

    private static string GetTestAssemblyPath()
    {
        return typeof(CrossProcess_Shared_Tests).Assembly.Location;
    }

    // This method is called by child processes
    public static void CrossProcessWorker(string dbPath, int processId, int documentCount)
    {
        Console.WriteLine($"Worker {processId} starting with {documentCount} documents to write");

        using var db = new LiteDatabase(new ConnectionString
        {
            Filename = dbPath,
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

        Console.WriteLine($"Worker {processId} completed writing {documentCount} documents");
    }

    public static void CrossProcessCounter(string dbPath, int processId, int operationCount)
    {
        Console.WriteLine($"Counter {processId} starting with {operationCount} operations");

        using var db = new LiteDatabase(new ConnectionString
        {
            Filename = dbPath,
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

        Console.WriteLine($"Counter {processId} completed {operationCount} operations");
    }
}
