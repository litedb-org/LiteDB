using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB;

namespace LiteDB.RollbackRepro;

internal static class Program
{
    private const int HolderTransactionCount = 99;
    private const int DocumentWriteCount = 10_000;

    private static void Main()
    {
        var stopwatch = Stopwatch.StartNew();

        var databasePath = Path.Combine(AppContext.BaseDirectory, "rollback-crash.db");
        Console.WriteLine($"Database path: {databasePath}");

        if (File.Exists(databasePath))
        {
            Console.WriteLine("Deleting previous database file.");
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

        var holderThreads = StartGuardTransactions(db, holdersReady, releaseHolders);

        holdersReady.Wait();
        Console.WriteLine($"Spawned {HolderTransactionCount} background transactions to exhaust the shared transaction memory pool.");

        try
        {
            RunFailingTransaction(db, collection);
        }
        catch (LiteException liteException)
        {
            Console.WriteLine();
            Console.WriteLine("Captured expected LiteDB.LiteException:");
            Console.WriteLine(liteException);
        }
        finally
        {
            releaseHolders.Set();

            foreach (var thread in holderThreads)
            {
                thread.Join();
            }

            stopwatch.Stop();
            Console.WriteLine($"Total elapsed time: {stopwatch.Elapsed}.");
        }
    }

    private static IReadOnlyList<Thread> StartGuardTransactions(LiteDatabase db, CountdownEvent ready, ManualResetEventSlim release)
    {
        var threads = new List<Thread>(HolderTransactionCount);

        for (var i = 0; i < HolderTransactionCount; i++)
        {
            var thread = new Thread(() => HoldTransaction(db, ready, release))
            {
                IsBackground = true,
                Name = $"Holder-{i:D2}"
            };

            thread.Start();
            threads.Add(thread);
        }

        return threads;
    }

    private static void HoldTransaction(LiteDatabase db, CountdownEvent ready, ManualResetEventSlim release)
    {
        var threadId = Thread.CurrentThread.ManagedThreadId;
        var began = false;

        try
        {
            began = db.BeginTrans();
            if (!began)
            {
                Console.WriteLine($"[{threadId}] BeginTrans returned false for holder transaction.");
            }
        }
        catch (LiteException ex)
        {
            Console.WriteLine($"[{threadId}] Failed to start holder transaction: {ex.Message}");
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
                Console.WriteLine($"[{threadId}] Holder rollback threw: {ex.Message}");
            }
        }
    }

    private static void RunFailingTransaction(LiteDatabase db, ILiteCollection<LargeDocument> collection)
    {
        Console.WriteLine();
        Console.WriteLine($"Starting write transaction on thread {Thread.CurrentThread.ManagedThreadId}.");

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

        var commitRequested = false;

        try
        {
            for (var i = 0; i < DocumentWriteCount; i++)
            {
                if (shouldTriggerSafepoint && !safepointTriggered)
                {
                    inspector ??= TransactionInspector.Attach(db);

                    Console.WriteLine();
                    Console.WriteLine($"Manually invoking safepoint before processing document #{i:N0}.");

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
                    Console.WriteLine($"Upserted {i:N0} documents...");
                }

                inspector ??= TransactionInspector.Attach(db);

                var currentSize = inspector.CurrentSize;
                maxSize = Math.Max(maxSize, inspector.MaxSize);

                if (!shouldTriggerSafepoint && currentSize >= maxSize)
                {
                    shouldTriggerSafepoint = true;
                    Console.WriteLine($"Queued safepoint after reaching transaction size {currentSize} at document #{i + 1:N0}.");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Simulating failure after safepoint flush.");
            throw new InvalidOperationException("Simulating transaction failure after safepoint flush.");
        }
        catch (Exception ex) when (ex is not LiteException)
        {
            Console.WriteLine($"Caught application exception: {ex.Message}");
            Console.WriteLine("Requesting rollback — this should trigger 'discarded page must be writable'.");

            var shareCounter = inspector?.GetCollectionShareCounter();
            if (shareCounter.HasValue)
            {
                Console.WriteLine($"Collection page share counter before rollback: {shareCounter.Value}.");
            }

            if (inspector is not null)
            {
                foreach (var (pageId, pageType, counter) in inspector.EnumerateWritablePages())
                {
                    Console.WriteLine($"Writable page {pageId} ({pageType}) share counter: {counter}.");
                }
            }

            db.Rollback();
            Console.WriteLine("Rollback returned without throwing — the bug did not reproduce.");
            throw;
        }
        finally
        {
            if (commitRequested)
            {
                db.Commit();
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

        public static TransactionInspector Attach(LiteDatabase db)
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
                ?? throw new InvalidOperationException("Current thread transaction is not available.");

            var pagesProperty = transaction.GetType().GetProperty("Pages", BindingFlags.Public | BindingFlags.Instance)
                                 ?? throw new InvalidOperationException("Transaction.Pages property not found.");

            var transactionPages = pagesProperty.GetValue(transaction)
                ?? throw new InvalidOperationException("Transaction pages are not available.");

            var transactionSizeProperty = transactionPages.GetType().GetProperty("TransactionSize", BindingFlags.Public | BindingFlags.Instance)
                                          ?? throw new InvalidOperationException("TransactionSize property not found.");

            var maxTransactionSizeProperty = transaction.GetType().GetProperty("MaxTransactionSize", BindingFlags.Public | BindingFlags.Instance)
                                             ?? throw new InvalidOperationException("MaxTransactionSize property not found.");

            var snapshotsProperty = transaction.GetType().GetProperty("Snapshots", BindingFlags.Public | BindingFlags.Instance)
                                     ?? throw new InvalidOperationException("Snapshots property not found.");

            if (snapshotsProperty.GetValue(transaction) is not IEnumerable<object> snapshots)
            {
                throw new InvalidOperationException("Snapshots collection not available.");
            }

            var snapshot = snapshots.Cast<object>().FirstOrDefault()
                ?? throw new InvalidOperationException("No snapshots available for the current transaction.");

            var collectionPageProperty = snapshot.GetType().GetProperty("CollectionPage", BindingFlags.Public | BindingFlags.Instance)
                                         ?? throw new InvalidOperationException("CollectionPage property not found.");

            var collectionPageType = collectionPageProperty.PropertyType;

            var bufferProperty = collectionPageType.GetProperty("Buffer", BindingFlags.Public | BindingFlags.Instance)
                                   ?? throw new InvalidOperationException("Buffer property not found on collection page.");

            var shareCounterField = bufferProperty.PropertyType.GetField("ShareCounter", BindingFlags.Public | BindingFlags.Instance)
                                     ?? throw new InvalidOperationException("ShareCounter field not found on page buffer.");

            var safepointMethod = transaction.GetType().GetMethod("Safepoint", BindingFlags.Public | BindingFlags.Instance)
                                   ?? throw new InvalidOperationException("Safepoint method not found on transaction service.");

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
    }
}
