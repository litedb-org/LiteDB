using System.Reflection;
using System.Runtime.InteropServices;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_2242_UnsyncableStorage;

/// <summary>
/// Regression since 5.0.21 of issue #2242, found in PR #3027 and fixed by the #3051 S09 extraction
/// (branch split/09-durability-protocol; guard: UnsyncableDataFile_Tests). Some
/// storage answers every fsync with EINVAL (file systems without sync support, e.g. FUSE or virtual
/// file systems), EROFS or ENOTSUP. 5.0.21 synced with FileStream.Flush(true), which on Unix ignores
/// exactly these errors, so a database there worked. The native device sync that replaced it reports
/// them, and only the WAL degraded: every data-file sync (creation, checkpoint, conversion) threw, so a
/// database on such storage could no longer be created or checkpointed. Fixed: data barriers degrade
/// like log barriers; with "Durable Commits=false" (the opt-out for such storage) the database is
/// created, indexed, written, checkpointed and reopened as with 5.0.21.
///
/// The repro makes every fsync and fdatasync of its own process fail with EINVAL, as such a mount
/// does: a seccomp filter answers those two system calls with EINVAL before the kernel runs them.
/// Nothing else changes, and the ReproRunner host is not affected. Linux (x64, arm64) only.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    private const int EINVAL = 22;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_2242_UnsyncableStorage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            NoSync.Install();
            host.SendLog(NoSync.Verify(Path.Combine(directory, "probe")));
            var (code, summary) = Run(host, Path.Combine(directory, "rows.db"));
            host.SendResult(code == Reproduced, summary);
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, $"INCONCLUSIVE: {error.GetType().Name}: {error.Message}", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return Inconclusive;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host, string path)
    {
        var optedOut = $"Filename={path};Durable Commits=false";
        var stage = "create";
        try
        {
            using (var db = new LiteDatabase(optedOut))
            {
                var rows = db.GetCollection("rows");
                stage = "index";
                rows.EnsureIndex("value");
                stage = "insert";
                rows.Insert(Enumerable.Range(0, 100).Select(i => new BsonDocument { ["_id"] = i, ["value"] = i % 7 }));
                stage = "checkpoint";
                db.Checkpoint();
                stage = "update";
                Require(rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = 100 }), "the update of _id 1 found no document");
                stage = "close";
            }
            stage = "reopen";
            using (var db = new LiteDatabase(optedOut))
            {
                var rows = db.GetCollection("rows");
                var (count, threes, updated) = (rows.Count(), rows.Count(Query.EQ("value", 3)), rows.Find(Query.EQ("value", 100)).Select(x => x["_id"].AsInt32).ToArray());
                Require(count == 100 && threes == 14 && updated.SequenceEqual(new[] { 1 }),
                    $"reopened: {count} rows, {threes} with value 3, value 100 at _id [{string.Join(",", updated)}] (expected 100, 14, [1])");
            }
        }
        catch (IOException error) when (error.Message.Contains($"failed (errno {EINVAL})", StringComparison.Ordinal))
        {
            host.SendLog($"{stage} threw {error.GetType().Name}: {error.Message}");
            return (Reproduced, $"REPRODUCED: on storage that cannot sync (fsync EINVAL) the database with Durable Commits=false failed at {stage}: " +
                $"{error.GetType().Name}: {error.Message}");
        }

        return (Fixed, "FIXED: on storage that cannot sync (fsync EINVAL) the database with Durable Commits=false was created, indexed, written, " +
            "checkpointed and reopened with every row and its index");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

/// <summary>A seccomp filter that answers fsync and fdatasync of every thread of this process with EINVAL.</summary>
internal static class NoSync
{
    private const int PR_SET_NO_NEW_PRIVS = 38;
    private const int SECCOMP_SET_MODE_FILTER = 1;
    private const int SECCOMP_FILTER_FLAG_TSYNC = 1;
    private const ushort BPF_LD_W_ABS = 0x20;
    private const ushort BPF_JMP_JEQ_K = 0x15;
    private const ushort BPF_RET_K = 0x06;
    private const uint SECCOMP_RET_ALLOW = 0x7FFF0000;
    private const uint SECCOMP_RET_ERRNO = 0x00050000;
    private const int EINVAL = 22;

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFilter
    {
        public ushort Code;
        public byte Jt;
        public byte Jf;
        public uint K;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFprog
    {
        public ushort Len;
        public IntPtr Filter;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, long operation, long flags, ref SockFprog program);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int fdatasync(int descriptor);

    public static void Install()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The repro needs a Linux seccomp filter.");
        // seccomp_data: int nr at offset 0, u32 arch at offset 4. AUDIT_ARCH_* and the syscall numbers per ABI.
        var (arch, fsyncNumber, fdatasyncNumber, seccompNumber) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (0xC000003Eu, 74u, 75u, 317L),
            Architecture.Arm64 => (0xC00000B7u, 82u, 83u, 277L),
            var other => throw new PlatformNotSupportedException($"No seccomp syscall table for {other}."),
        };
        var filter = new[]
        {
            new SockFilter { Code = BPF_LD_W_ABS, K = 4 },                              // A = arch
            new SockFilter { Code = BPF_JMP_JEQ_K, K = arch, Jt = 0, Jf = 3 },          // another ABI: allow
            new SockFilter { Code = BPF_LD_W_ABS, K = 0 },                              // A = syscall number
            new SockFilter { Code = BPF_JMP_JEQ_K, K = fsyncNumber, Jt = 2, Jf = 0 },   // fsync: EINVAL
            new SockFilter { Code = BPF_JMP_JEQ_K, K = fdatasyncNumber, Jt = 1, Jf = 0 }, // fdatasync: EINVAL
            new SockFilter { Code = BPF_RET_K, K = SECCOMP_RET_ALLOW },
            new SockFilter { Code = BPF_RET_K, K = SECCOMP_RET_ERRNO | EINVAL },
        };
        if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0)
            throw new InvalidOperationException($"prctl(PR_SET_NO_NEW_PRIVS) failed (errno {Marshal.GetLastPInvokeError()}).");
        var pinned = GCHandle.Alloc(filter, GCHandleType.Pinned);
        try
        {
            var program = new SockFprog { Len = (ushort)filter.Length, Filter = pinned.AddrOfPinnedObject() };
            // TSYNC applies the filter to every thread of the process, also the runtime's existing ones.
            var result = syscall(seccompNumber, SECCOMP_SET_MODE_FILTER, SECCOMP_FILTER_FLAG_TSYNC, ref program);
            if (result != 0)
                throw new InvalidOperationException($"seccomp(SECCOMP_SET_MODE_FILTER) returned {result} (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>Proves that a real file of this process can no longer be synced, from another thread too.</summary>
    public static string Verify(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        stream.WriteByte(1);
        stream.Flush(false);
        var descriptor = (int)stream.SafeFileHandle.DangerousGetHandle();
        var results = Task.Run(() => (fsync(descriptor), Marshal.GetLastPInvokeError(), fdatasync(descriptor), Marshal.GetLastPInvokeError())).Result;
        if (results != (-1, EINVAL, -1, EINVAL))
            throw new InvalidOperationException($"the seccomp filter is not in effect: fsync/fdatasync returned {results}.");
        return "Storage that cannot sync: every fsync and fdatasync of this process now fails with EINVAL (errno 22).";
    }
}
