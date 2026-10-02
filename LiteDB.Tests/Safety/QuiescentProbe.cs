using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// What a database left behind once every participant of a scenario stopped. <see cref="Clean"/>
    /// is false when a LiteDB thread outlived the grace period, a handle to the database or a
    /// companion file is still open, the file's Shared mutex (or its turnstile) is still held, a
    /// live reader lease remains, or sort scratch (<c>-tmp</c>) remains. Checks a platform cannot
    /// perform are listed in <see cref="Gaps"/>.
    /// </summary>
    internal sealed class QuiescentResult
    {
        public string Path { get; set; }
        /// <summary>LiteDB-named threads after the wait; -1 when the platform cannot enumerate them.</summary>
        public int Threads { get; set; }
        public string[] ThreadNames { get; set; } = new string[0];
        /// <summary>Open handles to the database or a companion; -1 when the platform cannot list them.</summary>
        public int OpenFds { get; set; }
        public string[] Handles { get; set; } = new string[0];
        public bool MutexFree { get; set; }
        public bool TurnstileFree { get; set; }
        public bool MutexAbandoned { get; set; }
        /// <summary>Reader leases that are still locked by an open handle.</summary>
        public int ReaderRegistry { get; set; }
        public string[] Companions { get; set; } = new string[0];
        /// <summary>Sort scratch (<c>&lt;stem&gt;-tmp&lt;ext&gt;</c>) still present.</summary>
        public bool Scratch { get; set; }
        public double WaitedMs { get; set; }
        public string[] Gaps { get; set; } = new string[0];
        public string[] Violations { get; set; } = new string[0];
        public bool Clean => this.Violations.Length == 0;
    }

    /// <summary>
    /// Quiescence oracle, evaluated only at scenario end after every participant stopped and every
    /// owner closed (a single connection's dispose says nothing about another connection to the
    /// same file; see <see cref="ConnectionCleanProbe"/>). Idle cap and grace come from the code:
    /// LiteDB starts no thread that outlives its connections except the Shared mutex owner thread,
    /// which exits once it has held nothing for <c>SharedMutexOwner.HolderIdle</c>, checking every
    /// <c>SharedMutexOwner.Poll</c>. So the cap is zero LiteDB threads, reached within
    /// HolderIdle + 2 x Poll, plus a scheduling allowance for a loaded machine.
    /// </summary>
    internal static class QuiescentProbe
    {
        public const int IdleThreadCap = 0;
        public static readonly TimeSpan SchedulingAllowance = TimeSpan.FromMilliseconds(500);
        public static readonly TimeSpan Grace = OwnerTiming("HolderIdle") +
            OwnerTiming("Poll") + OwnerTiming("Poll") +
            SchedulingAllowance;
        // Thread names are "LiteDB ..." (SharedMutexOwner, SharedMutexPin, coordinator); Linux
        // truncates the OS name to 15 characters, which keeps this prefix.
        private const string ThreadPrefix = "LiteDB ";
        private static readonly string[] CompanionSuffixes = { "-log", "-tmp", "-backup", "-temp", "-rebuild" };

        public static QuiescentResult Evaluate(string path, int allowedThreads = IdleThreadCap,
            SharedMutexNameStrategy strategy = SharedMutexNameStrategy.Default)
        {
            var full = System.IO.Path.GetFullPath(path);
            var result = new QuiescentResult { Path = full };
            var gaps = new List<string>();
            var violations = new List<string>();
            var waited = Stopwatch.StartNew();

            var names = LiteDbThreads(gaps);
            while (names != null && names.Length > allowedThreads && waited.Elapsed < Grace)
            {
                Thread.Sleep(20);
                names = LiteDbThreads(gaps);
            }
            result.WaitedMs = waited.Elapsed.TotalMilliseconds;
            result.Threads = names?.Length ?? -1;
            result.ThreadNames = names ?? new string[0];
            if (names != null && names.Length > allowedThreads)
                violations.Add($"threads: {names.Length} LiteDB thread(s) after {Grace.TotalMilliseconds:F0} ms " +
                    $"(cap {allowedThreads}): {string.Join(", ", names)}");

            result.Handles = OpenHandles(full, gaps);
            result.OpenFds = result.Handles == null ? -1 : result.Handles.Length;
            if (result.Handles == null) result.Handles = new string[0];
            else if (result.Handles.Length > 0)
                violations.Add("handles: still open: " + string.Join(", ", result.Handles));

            var name = SharedMutexNameFactory.Create(full, strategy);
            result.MutexFree = TryAcquire(name, out var abandoned);
            result.TurnstileFree = TryAcquire(name + ".Turn", out var turnAbandoned);
            result.MutexAbandoned = abandoned || turnAbandoned;
            if (!result.MutexFree) violations.Add("mutex: the database's Shared mutex is still held");
            if (!result.TurnstileFree) violations.Add("mutex: the Shared mutex turnstile is still held");

            result.ReaderRegistry = LiveLeases(full, out var leases);
            if (result.ReaderRegistry > 0) violations.Add("readers: live reader lease(s): " + string.Join(", ", leases));
            result.Companions = Companions(full);
            result.Scratch = File.Exists(ScratchPath(full));
            if (result.Scratch) violations.Add("scratch: sort scratch " + System.IO.Path.GetFileName(ScratchPath(full)) + " remains");
            result.Gaps = gaps.Distinct().ToArray();
            result.Violations = violations.ToArray();
            return result;
        }

        /// <summary>Names of live LiteDB threads, or null when the platform cannot enumerate them.</summary>
        internal static string[] LiteDbThreads(List<string> gaps)
        {
            const string tasks = "/proc/self/task";
            if (!Directory.Exists(tasks))
            {
                gaps.Add("threads: OS thread names are not enumerable in-process on this platform");
                return null;
            }
            var names = new List<string>();
            foreach (var task in Directory.GetDirectories(tasks))
            {
                try
                {
                    var comm = File.ReadAllText(System.IO.Path.Combine(task, "comm")).Trim();
                    if (comm.StartsWith(ThreadPrefix, StringComparison.Ordinal)) names.Add(comm);
                }
                catch (IOException) { /* the thread exited meanwhile */ }
                catch (UnauthorizedAccessException) { }
            }
            return names.ToArray();
        }

        /// <summary>Open handles to the database family, or null when the platform cannot list them.</summary>
        internal static string[] OpenHandles(string full, List<string> gaps)
        {
            const string fds = "/proc/self/fd";
            if (Directory.Exists(fds))
            {
                var open = new List<string>();
                foreach (var fd in Directory.GetFiles(fds))
                {
                    var target = ReadLink(fd);
                    if (target == null) continue;
                    if (target.EndsWith(" (deleted)", StringComparison.Ordinal)) target = target.Substring(0, target.Length - 10);
                    if (BelongsTo(full, target)) open.Add(target);
                }
                return open.ToArray();
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // An exclusive open fails while any handle without delete/write sharing remains.
                return new[] { full }.Concat(CompanionPaths(full)).Where(File.Exists)
                    .Where(file => !CanOpenExclusively(file)).ToArray();
            }
            gaps.Add("handles: no in-process handle listing on this platform (POSIX locks are advisory)");
            return null;
        }

        internal static bool BelongsTo(string full, string candidate)
        {
            if (string.Equals(candidate, full, StringComparison.Ordinal)) return true;
            if (candidate.StartsWith(full + "-", StringComparison.Ordinal)) return true; // -readers/, -shared-*
            var directory = System.IO.Path.GetDirectoryName(full);
            var stem = System.IO.Path.Combine(directory ?? "", System.IO.Path.GetFileNameWithoutExtension(full));
            return CompanionSuffixes.Any(suffix => candidate.StartsWith(stem + suffix, StringComparison.Ordinal));
        }

        /// <summary>The sort scratch file LiteDB creates next to the database (FileHelper.GetTempFile).</summary>
        internal static string ScratchPath(string path) => FileHelper.GetTempFile(System.IO.Path.GetFullPath(path));

        /// <summary>
        /// ScratchLive: while a reader whose sort spilled to disk is still live, its scratch file
        /// must exist (deleting it under the reader loses the reader's data). Returns the violation,
        /// or null when the scratch is present.
        /// </summary>
        public static string ScratchLive(string path)
        {
            var scratch = ScratchPath(path);
            return File.Exists(scratch) ? null
                : $"scratch: {System.IO.Path.GetFileName(scratch)} is missing while a reader with a spilled sort is live";
        }

        internal static string[] Companions(string full) =>
            CompanionPaths(full).Select(System.IO.Path.GetFileName).ToArray();

        private static string[] CompanionPaths(string full)
        {
            var directory = System.IO.Path.GetDirectoryName(full);
            if (directory == null || !Directory.Exists(directory)) return new string[0];
            return Directory.GetFiles(directory)
                .Where(file => !string.Equals(file, full, StringComparison.Ordinal) && BelongsTo(full, file))
                .OrderBy(file => file, StringComparer.Ordinal).ToArray();
        }

        private static int LiveLeases(string full, out string[] leases)
        {
            var directory = full + "-readers";
            leases = Directory.Exists(directory)
                ? Directory.GetFiles(directory).Where(file => file.EndsWith(".lease", StringComparison.Ordinal))
                    .Where(file => !CanOpenExclusively(file)).Select(System.IO.Path.GetFileName).ToArray()
                : new string[0];
            return leases.Length;
        }

        private static bool CanOpenExclusively(string file)
        {
            try
            {
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>The Shared writer mutex of <paramref name="path"/>, opened (or created) under the name LiteDB uses.</summary>
        internal static Mutex OpenMutex(string path, SharedMutexNameStrategy strategy = SharedMutexNameStrategy.Default) =>
            SharedMutexFactory.Create(SharedMutexNameFactory.Create(System.IO.Path.GetFullPath(path), strategy));

        /// <summary>WaitOne(0) on a fresh thread, so recursion of the caller's thread cannot fake a free mutex.</summary>
        internal static bool TryAcquire(string name, out bool abandoned)
        {
            var free = false;
            var wasAbandoned = false;
            Exception error = null;
            var probe = new Thread(() =>
            {
                try
                {
                    using (var mutex = SharedMutexFactory.Create(name))
                    {
                        try { free = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { free = wasAbandoned = true; }
                        if (free) mutex.ReleaseMutex();
                    }
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "safety mutex probe" };
            probe.Start();
            probe.Join();
            if (error != null) throw new InvalidOperationException("The Shared mutex probe failed: " + error.Message, error);
            abandoned = wasAbandoned;
            return free;
        }

        private static string ReadLink(string path)
        {
#if NET6_0_OR_GREATER
            try { return new FileInfo(path).LinkTarget; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
#else
            return null;
#endif
        }

        /// <summary>Reads the owner thread's timing from the code; a renamed field must fail loudly, not drift.</summary>
        private static TimeSpan OwnerTiming(string field)
        {
            var value = typeof(SharedMutexOwner).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            return value is TimeSpan span ? span
                : throw new InvalidOperationException($"SharedMutexOwner.{field} no longer exists; update QuiescentProbe.Grace.");
        }
    }
}
