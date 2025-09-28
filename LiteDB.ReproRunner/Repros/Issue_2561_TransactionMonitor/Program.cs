using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_2561_TransactionMonitor;

internal static class Program
{
    private const string DefaultDatabaseName = "issue2561.db";

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        var context = ReproContext.FromEnvironment();

        host.SendLifecycle("starting", new
        {
            context.InstanceIndex,
            context.TotalInstances,
            context.SharedDatabaseRoot
        });

        try
        {
            Run(host, context);
            host.SendResult(true, "Repro succeeded: ReleaseTransaction threw on the wrong thread.");
            host.SendLifecycle("completed", new { Success = true });
            return 0;
        }
        catch (Exception ex)
        {
            host.SendLog($"Reproduction failed: {ex}", ReproHostLogLevel.Error);
            host.SendResult(false, "Reproduction failed.", new { Exception = ex.ToString() });
            host.SendLifecycle("completed", new { Success = false });
            Console.Error.WriteLine("Reproduction failed: {0}", ex);
            return 1;
        }
    }

    private static void Run(ReproHostClient host, ReproContext context)
    {
        var databaseDirectory = string.IsNullOrWhiteSpace(context.SharedDatabaseRoot)
            ? AppContext.BaseDirectory
            : context.SharedDatabaseRoot;

        Directory.CreateDirectory(databaseDirectory);

        var databasePath = Path.Combine(databaseDirectory, DefaultDatabaseName);
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        Log(host, $"Using database: {databasePath}");

        var connectionString = new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Direct
        };

        using var database = new LiteDatabase(connectionString);
        var collection = database.GetCollection<BsonDocument>("docs");

        var engineField = typeof(LiteDatabase).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException("LiteDatabase._engine field not found.");
        var engine = engineField.GetValue(database) ?? throw new InvalidOperationException("LiteDatabase engine unavailable.");
        var engineType = engine.GetType();

        var beginTrans = engineType.GetMethod("BeginTrans", BindingFlags.Public | BindingFlags.Instance)
                         ?? throw new InvalidOperationException("LiteEngine.BeginTrans method not found.");
        var rollback = engineType.GetMethod("Rollback", BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new InvalidOperationException("LiteEngine.Rollback method not found.");
        var monitorField = engineType.GetField("_monitor", BindingFlags.NonPublic | BindingFlags.Instance)
                          ?? throw new InvalidOperationException("LiteEngine._monitor field not found.");
        var monitor = monitorField.GetValue(engine) ?? throw new InvalidOperationException("Transaction monitor unavailable.");
        var monitorType = monitor.GetType();

        var transactionsProperty = monitorType.GetProperty("Transactions", BindingFlags.Public | BindingFlags.Instance)
                                 ?? throw new InvalidOperationException("TransactionMonitor.Transactions property not found.");
        var releaseTransaction = monitorType.GetMethod("ReleaseTransaction", BindingFlags.Public | BindingFlags.Instance)
                               ?? throw new InvalidOperationException("TransactionMonitor.ReleaseTransaction method not found.");

        Log(host, "Opening explicit transaction on main thread...");

        if (!(beginTrans.Invoke(engine, Array.Empty<object>()) is bool began) || !began)
        {
            throw new InvalidOperationException("BeginTrans returned false; the transaction was not created.");
        }

        collection.Insert(new BsonDocument
        {
            ["_id"] = ObjectId.NewObjectId(),
            ["description"] = "Issue 2561 transaction monitor repro",
            ["createdAt"] = DateTime.UtcNow
        });

        var transactions = (IEnumerable<object>)(transactionsProperty.GetValue(monitor)
                              ?? throw new InvalidOperationException("Transaction list is unavailable."));

        var capturedTransaction = transactions.Single();
        var reproObserved = new ManualResetEventSlim(false);
        LiteException? observedException = null;

        var worker = new Thread(() =>
        {
            try
            {
                releaseTransaction.Invoke(monitor, new[] { capturedTransaction });
                Log(host, "ReleaseTransaction completed without throwing.", ReproHostLogLevel.Warning);
            }
            catch (TargetInvocationException invocationException) when (invocationException.InnerException is LiteException liteException)
            {
                observedException = liteException;
                Log(host, "Observed LiteException from ReleaseTransaction on worker thread:");
                Log(host, liteException.ToString());
            }
            finally
            {
                reproObserved.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Issue2561-Repro-Worker"
        };

        worker.Start();

        if (!reproObserved.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Timed out waiting for ReleaseTransaction to finish.");
        }

        rollback.Invoke(engine, Array.Empty<object>());

        if (observedException is null)
        {
            throw new InvalidOperationException("Expected LiteException was not observed.");
        }

        if (!observedException.Message.Contains("current thread must contains transaction parameter", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"LiteException did not contain expected message. Actual: {observedException.Message}");
        }

        Log(host, "Repro succeeded: ReleaseTransaction threw on the wrong thread.");
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
