using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_2586_RollbackTransaction;

/// <summary>
/// Repro of LiteDB issue #2586. The repro returns exit code 0 when the rollback throws the expected
/// LiteException and non-zero when the bug fails to reproduce.
/// </summary>
internal static class Program
{
    private const int HolderTransactionCount = 99;
    private const int DocumentWriteCount = 10_000;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();

        host.SendLifecycle("starting", new
        {
            context.InstanceIndex,
            context.TotalInstances,
            context.SharedDatabaseRoot
        });

        try
        {
            var reproduced = Run(host, context);
            host.SendResult(reproduced, reproduced
                ? "Repro succeeded: rollback threw with exhausted transaction pool."
                : "Repro did not reproduce the rollback exception.");
            host.SendLifecycle("completed", new { Success = reproduced });
            return reproduced ? 0 : 1;
        }
        catch (Exception ex)
        {
            host.SendLog($"Reproduction failed: {ex}", ReproHostLogLevel.Error);
            host.SendResult(false, "Reproduction failed.", new { Exception = ex.ToString() });
            host.SendLifecycle("completed", new { Success = false });
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static bool Run(ReproHostClient host, ReproContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        var databaseDirectory = string.IsNullOrWhiteSpace(context.SharedDatabaseRoot)
            ? AppContext.BaseDirectory
            : context.SharedDatabaseRoot;

        Directory.CreateDirectory(databaseDirectory);

        var databasePath = Path.Combine(databaseDirectory, "rollback-crash.db");
        Log(host, $"Database path: {databasePath}");

        if (File.Exists(databasePath))
        {
            Log(host, "Deleting previous database file.");
            File.Delete(databasePath);
        }

        var connectionString = new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Direct
        };

        using var db = new LiteDatabase(connectionString);
        var collection = db.GetCollection<LargeDocument>("documents");

        using var releaseHolders = new ManualResetEventSlim(false);
        using var holdersReady = new CountdownEvent(HolderTransactionCount);

        var holderThreads = StartGuardTransactions(host, db, holdersReady, releaseHolders);

        holdersReady.Wait();
        Log(host, $"Spawned {HolderTransactionCount} background transactions to exhaust the shared transaction memory pool.");

        try
        {
            return RunFailingTransaction(host, db, collection);
        }
        finally
        {
            releaseHolders.Set();

            foreach (var thread in holderThreads)
            {
                thread.Join();
            }

            stopwatch.Stop();
            Log(host, $"Total elapsed time: {stopwatch.Elapsed}.");
        }
    }

    private static IReadOnlyList<Thread> StartGuardTransactions(ReproHostClient host, LiteDatabase db, CountdownEvent ready, ManualResetEventSlim release)
    {
        var threads = new List<Thread>(HolderTransactionCount);

        for (var i = 0; i < HolderTransactionCount; i++)
        {
            var thread = new Thread(() => HoldTransaction(host, db, ready, release))
            {
                IsBackground = true,
                Name = $"Holder-{i:D2}"
            };

            thread.Start();
            threads.Add(thread);
        }

        return threads;
    }

    private static void HoldTransaction(ReproHostClient host, LiteDatabase db, CountdownEvent ready, ManualResetEventSlim release)
    {
        var threadId = Thread.CurrentThread.ManagedThreadId;
        var began = false;

        try
        {
            began = db.BeginTrans();
            if (!began)
            {
                Log(host, $"[{threadId}] BeginTrans returned false for holder transaction.", ReproHostLogLevel.Warning);
            }
        }
        catch (LiteException ex)
        {
            Log(host, $"[{threadId}] Failed to start holder transaction: {ex.Message}", ReproHostLogLevel.Warning);
        }
        finally
        {
            ready.Signal();
        }

        if (!began)
        {
            return;
        }

        try
        {
            release.Wait();
        }
        finally
        {
            try
            {
                db.Rollback();
            }
            catch (LiteException ex)
            {
                Log(host, $"[{threadId}] Holder rollback threw: {ex.Message}", ReproHostLogLevel.Warning);
            }
        }
    }

    private static bool RunFailingTransaction(ReproHostClient host, LiteDatabase db, ILiteCollection<LargeDocument> collection)
    {
        Console.WriteLine();
        Log(host, $"Starting write transaction on thread {Thread.CurrentThread.ManagedThreadId}.");

        if (!db.BeginTrans())
        {
            throw new InvalidOperationException("Failed to begin primary transaction for reproduction.");
        }

        TransactionInspector? inspector = null;
        var maxSize = 0;
        var safepointTriggered = false;
        var shouldTriggerSafepoint = false;

        var payloadA = new string('A', 4_096);
        var payloadB = new string('B', 4_096);
        var payloadC = new string('C', 2_048);
        var largeBinary = new byte[128 * 1024];

        try
        {
            for (var i = 0; i < DocumentWriteCount; i++)
            {
                if (shouldTriggerSafepoint && !safepointTriggered)
                {
                    inspector ??= TransactionInspector.Attach(db, collection.Name);

                    Console.WriteLine();
                    Log(host, $"Manually invoking safepoint before processing document #{i:N0}.");

                    inspector.InvokeSafepoint();
                    // Safepoint transitions all dirty buffers into the readable cache. Manually
                    // mark the collection page as readable to mirror the race condition described
                    // in the bug investigation before triggering the rollback path.
                    inspector.ForceCollectionPageShareCounter(1);

                    safepointTriggered = true;

                    throw new InvalidOperationException("Simulating transaction failure after safepoint flush.");
                }

                var document = new LargeDocument
                {
                    Id = i,
                    BatchId = Guid.NewGuid(),
                    CreatedUtc = DateTime.UtcNow,
                    Description = $"Large document #{i:N0}",
                    Payload1 = payloadA,
                    Payload2 = payloadB,
                    Payload3 = payloadC,
                    LargePayload = largeBinary
                };

                collection.Upsert(document);

                if (i % 100 == 0)
                {
                    Log(host, $"Upserted {i:N0} documents...");
                }

                inspector ??= TransactionInspector.Attach(db, collection.Name);

                var currentSize = inspector.CurrentSize;
                maxSize = Math.Max(maxSize, inspector.MaxSize);

                if (!shouldTriggerSafepoint && currentSize >= maxSize)
                {
                    shouldTriggerSafepoint = true;
                    Log(host, $"Queued safepoint after reaching transaction size {currentSize} at document #{i + 1:N0}.");
                }
            }

            Console.WriteLine();
            Log(host, "Simulating failure after safepoint flush.");
            throw new InvalidOperationException("Simulating transaction failure after safepoint flush.");
        }
        catch (Exception ex) when (ex is not LiteException)
        {
            Log(host, $"Caught application exception: {ex.Message}");
            Log(host, "Requesting rollback — this should trigger 'discarded page must be writable'.");

            var shareCounter = inspector?.GetCollectionShareCounter();
            if (shareCounter.HasValue)
            {
                Log(host, $"Collection page share counter before rollback: {shareCounter.Value}.");
            }

            if (inspector is not null)
            {
                foreach (var (pageId, pageType, counter) in inspector.EnumerateWritablePages())
                {
                    Log(host, $"Writable page {pageId} ({pageType}) share counter: {counter}.");
                }
            }

            try
            {
                db.Rollback();

                var previous = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Rollback returned without throwing — the bug did not reproduce.");
                Console.ForegroundColor = previous;

                Log(host, "Rollback returned without throwing — the bug did not reproduce.", ReproHostLogLevel.Warning);
                return false;
            }
            catch (LiteException liteException)
            {
                Console.WriteLine();
                Console.WriteLine("Captured expected LiteDB.LiteException:");
                Console.WriteLine(liteException);

                Log(host, "Captured expected LiteDB.LiteException:");
                Log(host, liteException.ToString(), ReproHostLogLevel.Warning);

                var color = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Rollback threw LiteException — the bug reproduced.");
                Console.ForegroundColor = color;

                Log(host, "Rollback threw LiteException — the bug reproduced.", ReproHostLogLevel.Error);
                return true;
            }
        }
    }

    private sealed class LargeDocument
    {
        public int Id { get; set; }
        public Guid BatchId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Payload1 { get; set; } = string.Empty;
        public string Payload2 { get; set; } = string.Empty;
        public string Payload3 { get; set; } = string.Empty;
        public byte[] LargePayload { get; set; } = Array.Empty<byte>();
    }

    private sealed class TransactionInspector
    {
        private readonly object _transaction;
        private readonly object _transactionPages;
        private readonly object _snapshot;
        private readonly PropertyInfo _transactionSizeProperty;
        private readonly PropertyInfo _maxTransactionSizeProperty;
        private readonly PropertyInfo _collectionPageProperty;
        private readonly PropertyInfo _bufferProperty;
        private readonly FieldInfo _shareCounterField;
        private readonly MethodInfo _safepointMethod;

        private TransactionInspector(
            object transaction,
            object transactionPages,
            object snapshot,
            PropertyInfo transactionSizeProperty,
            PropertyInfo maxTransactionSizeProperty,
            PropertyInfo collectionPageProperty,
            PropertyInfo bufferProperty,
            FieldInfo shareCounterField,
            MethodInfo safepointMethod)
        {
            _transaction = transaction;
            _transactionPages = transactionPages;
            _snapshot = snapshot;
            _transactionSizeProperty = transactionSizeProperty;
            _maxTransactionSizeProperty = maxTransactionSizeProperty;
            _collectionPageProperty = collectionPageProperty;
            _bufferProperty = bufferProperty;
            _shareCounterField = shareCounterField;
            _safepointMethod = safepointMethod;
        }

        public int CurrentSize => (int)_transactionSizeProperty.GetValue(_transactionPages)!;

        public int MaxSize => (int)_maxTransactionSizeProperty.GetValue(_transaction)!;

        public int? GetCollectionShareCounter()
        {
            var collectionPage = _collectionPageProperty.GetValue(_snapshot);

            if (collectionPage is null)
            {
                return null;
            }

            var buffer = _bufferProperty.GetValue(collectionPage);

            return buffer is null ? null : (int?)_shareCounterField.GetValue(buffer);
        }

        public void InvokeSafepoint()
        {
            _safepointMethod.Invoke(_transaction, Array.Empty<object>());
        }

        public void ForceCollectionPageShareCounter(int shareCounter)
        {
            var collectionPage = _collectionPageProperty.GetValue(_snapshot);

            if (collectionPage is null)
            {
                return;
            }

            var buffer = _bufferProperty.GetValue(collectionPage);

            if (buffer is null)
            {
                return;
            }

            _shareCounterField.SetValue(buffer, shareCounter);
        }

        public IEnumerable<(uint PageId, string PageType, int ShareCounter)> EnumerateWritablePages()
        {
            var getPagesMethod = _snapshot.GetType().GetMethod(
                "GetWritablePages",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                new[] { typeof(bool), typeof(bool) },
                modifiers: null)
                ?? throw new InvalidOperationException("GetWritablePages method not found on snapshot.");

            if (getPagesMethod.Invoke(_snapshot, new object[] { true, true }) is not IEnumerable<object> pages)
            {
                yield break;
            }

            foreach (var page in pages)
            {
                var pageIdProperty = page.GetType().GetProperty("PageID", BindingFlags.Public | BindingFlags.Instance)
                                     ?? throw new InvalidOperationException("PageID property not found on page.");

                var pageTypeProperty = page.GetType().GetProperty("PageType", BindingFlags.Public | BindingFlags.Instance)
                                       ?? throw new InvalidOperationException("PageType property not found on page.");

                var bufferProperty = page.GetType().GetProperty("Buffer", BindingFlags.Public | BindingFlags.Instance)
                                     ?? throw new InvalidOperationException("Buffer property not found on page.");

                var buffer = bufferProperty.GetValue(page);

                if (buffer is null)
                {
                    continue;
                }

                var pageId = (uint)pageIdProperty.GetValue(page)!;
                var pageTypeName = pageTypeProperty.GetValue(page)?.ToString() ?? "<unknown>";
                var shareCounter = (int)_shareCounterField.GetValue(buffer)!;

                yield return (pageId, pageTypeName, shareCounter);
            }
        }

        public static TransactionInspector Attach(LiteDatabase db, string collectionName)
        {
            var engineField = typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? throw new InvalidOperationException("Unable to locate LiteDatabase engine field.");

            var engine = engineField.GetValue(db) ?? throw new InvalidOperationException("LiteDatabase engine is not initialized.");

            var monitorField = engine.GetType().GetField("_monitor", BindingFlags.NonPublic | BindingFlags.Instance)
                               ?? throw new InvalidOperationException("Unable to locate TransactionMonitor field.");

            var monitor = monitorField.GetValue(engine) ?? throw new InvalidOperationException("TransactionMonitor is unavailable.");

            var getThreadTransaction = monitor.GetType().GetMethod("GetThreadTransaction", BindingFlags.Public | BindingFlags.Instance)
                                       ?? throw new InvalidOperationException("GetThreadTransaction method not found.");

            var transaction = getThreadTransaction.Invoke(monitor, Array.Empty<object>())
                              ?? throw new InvalidOperationException("Thread transaction is not available.");

            var transactionType = transaction.GetType();

            var transactionPages = GetTransactionPages(transactionType, transaction)
                                  ?? throw new InvalidOperationException("Transaction pages are unavailable.");

            var snapshot = GetSnapshot(transactionType, transaction, collectionName)
                           ?? throw new InvalidOperationException("Transaction snapshot is unavailable.");

            var transactionSizeProperty = transactionPages.GetType().GetProperty("TransactionSize", BindingFlags.Public | BindingFlags.Instance)
                                           ?? throw new InvalidOperationException("TransactionSize property not found.");

            var maxTransactionSizeProperty = transaction.GetType().GetProperty("MaxTransactionSize", BindingFlags.Public | BindingFlags.Instance)
                                             ?? throw new InvalidOperationException("MaxTransactionSize property not found.");

            var collectionPageProperty = snapshot.GetType().GetProperty("CollectionPage", BindingFlags.Public | BindingFlags.Instance)
                                        ?? throw new InvalidOperationException("CollectionPage property not found on snapshot.");

            var bufferProperty = collectionPageProperty.PropertyType.GetProperty("Buffer", BindingFlags.Public | BindingFlags.Instance)
                                   ?? throw new InvalidOperationException("Buffer property not found on collection page.");

            var shareCounterField = bufferProperty.PropertyType.GetField("ShareCounter", BindingFlags.Public | BindingFlags.Instance)
                                      ?? throw new InvalidOperationException("ShareCounter field not found on buffer.");

            var safepointMethod = transaction.GetType().GetMethod("Safepoint", BindingFlags.Public | BindingFlags.Instance)
                                  ?? throw new InvalidOperationException("Safepoint method not found on transaction.");

            return new TransactionInspector(
                transaction,
                transactionPages,
                snapshot,
                transactionSizeProperty,
                maxTransactionSizeProperty,
                collectionPageProperty,
                bufferProperty,
                shareCounterField,
                safepointMethod);
        }

        private static object? GetTransactionPages(Type transactionType, object transaction)
        {
            var field = transactionType.GetField("_transactionPages", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? transactionType.GetField("_transPages", BindingFlags.NonPublic | BindingFlags.Instance);

            if (field?.GetValue(transaction) is { } pages)
            {
                return pages;
            }

            var property = transactionType.GetProperty("Pages", BindingFlags.Public | BindingFlags.Instance);

            return property?.GetValue(transaction);
        }

        private static object? GetSnapshot(Type transactionType, object transaction, string collectionName)
        {
            var snapshot = transactionType.GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(transaction);

            if (snapshot is not null)
            {
                return snapshot;
            }

            var snapshotsField = transactionType.GetField("_snapshots", BindingFlags.NonPublic | BindingFlags.Instance);

            if (snapshotsField?.GetValue(transaction) is IDictionary dictionary)
            {
                snapshot = FindSnapshot(dictionary, collectionName);

                if (snapshot is not null)
                {
                    return snapshot;
                }
            }

            var snapshotsProperty = transactionType.GetProperty("Snapshots", BindingFlags.Public | BindingFlags.Instance);

            if (snapshotsProperty?.GetValue(transaction) is IEnumerable enumerable)
            {
                return FindSnapshot(enumerable, collectionName);
            }

            return null;
        }

        private static object? FindSnapshot(IDictionary dictionary, string collectionName)
        {
            if (!string.IsNullOrWhiteSpace(collectionName) && dictionary.Contains(collectionName))
            {
                var value = dictionary[collectionName];

                if (value is not null)
                {
                    return value;
                }
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is null)
                {
                    continue;
                }

                if (MatchesCollection(entry.Value, collectionName))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        private static object? FindSnapshot(IEnumerable snapshots, string collectionName)
        {
            foreach (var snapshot in snapshots)
            {
                if (snapshot is null)
                {
                    continue;
                }

                if (MatchesCollection(snapshot, collectionName))
                {
                    return snapshot;
                }
            }

            return null;
        }

        private static bool MatchesCollection(object snapshot, string collectionName)
        {
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                return true;
            }

            var nameProperty = snapshot.GetType().GetProperty("CollectionName", BindingFlags.Public | BindingFlags.Instance);
            var name = nameProperty?.GetValue(snapshot)?.ToString();

            return string.Equals(name, collectionName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void Log(ReproHostClient host, string message, ReproHostLogLevel level = ReproHostLogLevel.Information)
    {
        host.SendLog(message, level);

        if (level >= ReproHostLogLevel.Warning)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            Console.WriteLine(message);
        }
    }
}
