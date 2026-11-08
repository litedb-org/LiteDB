using System.Runtime.InteropServices;
using LiteDB.Client.Shared;

namespace LiteDB.Fuzz.Targets;

/// <summary>Exercise actual futex waits and injected return errors on mapped control storage.</summary>
internal sealed class SharedWakeFuzzer : IFuzzTarget
{
    public string Name => "shared-wake";
    public string Description => "Mapped Linux wake error dispositions and mapping integrity.";

    public Task RunAsync(FuzzContext context)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return Task.CompletedTask;
        var path = context.RegisterFile(Path.Combine(context.DirectoryPath, "wake.map"));
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        file.SetLength(SharedMemoryLayout.WriterWake + 4096);
        using var map = new SharedMemoryMap(file, file.Length);
        var errors = new[] { 0, 4, 11, 110, 5 };
        while (context.Next())
        {
            foreach (var error in errors)
            {
                var value = context.Random.Next();
                map.Store(0, value);
                var injected = false;
                map.SimulateWaitError = _ => { injected = true; return error; };
                Exception failure = null;
                try { map.WaitWake(map.Load(SharedMemoryLayout.WriterWake), 1); }
                catch (Exception caught) { failure = caught; }
                finally { map.SimulateWaitError = null; }
                context.Check(injected, "Mapped wait did not reach its configured return hook.");
                context.Check(error == 5 ? failure is IOException : failure == null,
                    "Mapped wait accepted an unexpected error or rejected a retryable result.");
                context.Check(map.Load(0) == value, "Mapped wait modified unrelated control bytes.");
                var previous = map.Load(SharedMemoryLayout.WriterWake);
                map.Wake();
                context.Check(map.Load(SharedMemoryLayout.WriterWake) == previous + 1,
                    "Wake did not advance after an injected wait result.");
                context.Trace("wait-result", new { error, value });
            }
        }
        return Task.CompletedTask;
    }
}
