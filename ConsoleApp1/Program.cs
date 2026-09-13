using System.IO;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;

const string DatabaseName = "issue-2561-repro.db";
var databasePath = Path.Combine(AppContext.BaseDirectory, DatabaseName);

if (File.Exists(databasePath))
{
    File.Delete(databasePath);
}

var settings = new EngineSettings
{
    Filename = databasePath
};

Console.WriteLine($"LiteDB engine file: {databasePath}");
Console.WriteLine("Creating an explicit transaction on the main thread...");

using var engine = new LiteEngine(settings);
engine.BeginTrans();

var monitorField = typeof(LiteEngine).GetField("_monitor", BindingFlags.NonPublic | BindingFlags.Instance) ??
    throw new InvalidOperationException("Could not locate the transaction monitor field.");

var monitor = monitorField.GetValue(engine) ??
    throw new InvalidOperationException("Failed to extract the transaction monitor instance.");

var getTransaction = monitor.GetType().GetMethod(
    "GetTransaction",
    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
    binder: null,
    types: new[] { typeof(bool), typeof(bool), typeof(bool).MakeByRefType() },
    modifiers: null) ??
    throw new InvalidOperationException("Could not locate GetTransaction on the monitor.");

var getTransactionArgs = new object[] { false, false, null! };
var transaction = getTransaction.Invoke(monitor, getTransactionArgs) ??
    throw new InvalidOperationException("LiteDB did not return the thread-bound transaction.");

Console.WriteLine("Main thread transaction captured. Launching a worker thread to mimic the finalizer...");

var releaseTransaction = monitor.GetType().GetMethod(
    "ReleaseTransaction",
    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
    binder: null,
    types: new[] { transaction.GetType() },
    modifiers: null) ??
    throw new InvalidOperationException("Could not locate ReleaseTransaction on the monitor.");

var worker = new Thread(() =>
{
    Console.WriteLine($"Worker thread {Environment.CurrentManagedThreadId} releasing the transaction...");

    // This invocation throws LiteException("current thread must contains transaction parameter")
    // because the worker thread never registered the transaction in its ThreadLocal slot.
    releaseTransaction.Invoke(monitor, new[] { transaction });
});

worker.Start();
worker.Join();

Console.WriteLine("If you see this message the repro did not trigger the crash.");

