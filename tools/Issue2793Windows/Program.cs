using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;

using LiteDB;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 2) return 20;
        var directory = Path.GetFullPath(args[1]);
        var path = Path.Combine(directory, "shared.db");
        try
        {
            if (args[0] == "owner") Owner(directory, path);
            else if (args[0] == "peer") return Peer(directory, path);
            else throw new ArgumentException("Expected owner or peer");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, args[0] + "-error.txt"), error.ToString());
            return 20;
        }
    }

    private static LiteDatabase Open(string path, bool shared) => new LiteDatabase(new ConnectionString
    {
        Filename = path, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct
    });

    private static void Owner(string directory, string path)
    {
        using var database = Open(path, true);
        database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "owner-before-contention" });
        var engine = typeof(LiteDatabase).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(database);
        using var mutex = (Mutex)engine.GetType().GetField("_mutex", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
        var assembly = typeof(LiteDatabase).Assembly;
        GetKernelObjectSecurity(mutex.SafeWaitHandle, 7, null, 0, out var required);
        var descriptor = new byte[required];
        if (!GetKernelObjectSecurity(mutex.SafeWaitHandle, 7, descriptor, required, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        File.WriteAllText(Path.Combine(directory, "owner-platform.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            assembly = assembly.FullName, file = assembly.Location,
            logonSids = LogonSids(),
            runtime = RuntimeInformation.FrameworkDescription,
            mutexSecurity = new RawSecurityDescriptor(descriptor, 0).GetSddlForm(AccessControlSections.All)
        }));
        mutex.WaitOne();
        try
        {
            File.WriteAllText(Path.Combine(directory, "owner-ready"), WindowsIdentity.GetCurrent().User.Value);
            WaitFor(Path.Combine(directory, "release-owner"));
        }
        finally { mutex.ReleaseMutex(); }
        WaitFor(Path.Combine(directory, "peer-result.json"));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "peer-result.json")));
        var reproduced = result.RootElement.GetProperty("outcome").GetString() == "reported-failure";
        database.Dispose();
        using var reopened = Open(path, false);
        var rows = reopened.GetCollection("rows").FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
        Require(rows.Length == (reproduced ? 1 : 2), "wrong persisted row count");
        Require(rows[0]["_id"] == 1 && rows[0]["payload"] == "owner-before-contention", "owner payload changed");
        if (!reproduced) Require(rows[1]["_id"] == 2 && rows[1]["payload"] == "peer-after-release", "peer payload missing");
        File.WriteAllText(Path.Combine(directory, "owner-verified"), "reopened ledger passed");
    }

    private static int Peer(string directory, string path)
    {
        var owner = File.ReadAllText(Path.Combine(directory, "owner-ready"));
        var peer = WindowsIdentity.GetCurrent().User.Value;
        Require(owner != peer, "two distinct actual Windows accounts are required");
        using var ownerPlatform = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "owner-platform.json")));
        var ownerLogons = ownerPlatform.RootElement.GetProperty("logonSids").EnumerateArray().Select(item => item.GetString()).ToArray();
        var peerLogons = LogonSids();
        Require(owner == "S-1-5-18" && peer != "S-1-5-18",
            "the owner must be the actual LocalSystem service account and the peer a different account");
        // Direct mode opens this exact data file for read/write. A directory ACL
        // or file access failure must never count as the named-mutex defect.
        using (var direct = Open(path, false))
        {
            Require(direct.GetCollection("rows").Count() == 1, "direct control count");
            Require(direct.GetCollection("rows").FindById(1)["payload"] == "owner-before-contention", "direct control payload");
        }
        File.WriteAllText(Path.Combine(directory, "peer-ready"), peer);
        LiteDatabase shared;
        try { shared = Open(path, true); }
        catch (UnauthorizedAccessException error)
        {
            File.WriteAllText(Path.Combine(directory, "peer-result.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                owner, peer, ownerLogons, peerLogons, outcome = "reported-failure", stage = "SharedEngine constructor", error = error.ToString()
            }));
            return 0;
        }
        using (shared)
        {
            shared.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["payload"] = "peer-after-release" });
        }
        File.WriteAllText(Path.Combine(directory, "peer-result.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            owner, peer, ownerLogons, peerLogons, outcome = "passed", stage = "shared write complete"
        }));
        return 10;
    }

    private static void WaitFor(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException(path);
            Thread.Sleep(25);
        }
    }

    private static string[] LogonSids() => WindowsIdentity.GetCurrent().Groups
        .Select(group => group.Value).Where(value => value.StartsWith("S-1-5-5-")).ToArray();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeWaitHandle handle, uint information,
        byte[] descriptor, uint length, out uint required);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
