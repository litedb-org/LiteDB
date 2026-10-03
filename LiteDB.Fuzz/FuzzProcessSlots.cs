namespace LiteDB.Fuzz;

/// <summary>
/// An optional limit on target processes that every LiteDB.Fuzz invocation naming the same slot
/// directory shares. Each invocation already runs at most <see cref="FuzzProcessRunner.MaxParallelism"/>
/// target processes; several invocations started side by side (the PR-selected job runs one per count
/// group) would multiply that. With <c>LITEDB_FUZZ_SLOT_DIR</c> and <c>LITEDB_FUZZ_SLOTS</c> set, a
/// target run also holds one of that many slots for its whole duration. A slot is a lock file opened
/// exclusively, so the operating system frees the slots of an invocation that crashed or was killed.
/// Slots only bound concurrency: they never change a run's seed, input or trace.
/// </summary>
internal static class FuzzProcessSlots
{
    internal const string DirectoryVariable = "LITEDB_FUZZ_SLOT_DIR";
    internal const string CountVariable = "LITEDB_FUZZ_SLOTS";
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    /// <summary>The slot settings of this process's environment, or null when no shared limit is set.</summary>
    internal static (string Directory, int Count)? FromEnvironment()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        var text = Environment.GetEnvironmentVariable(CountVariable);
        if (string.IsNullOrEmpty(directory) && string.IsNullOrEmpty(text)) return null;
        if (string.IsNullOrEmpty(directory) || !int.TryParse(text, out var count) || count < 1)
        {
            throw new InvalidOperationException(
                $"{DirectoryVariable} and {CountVariable} must be set together ({CountVariable} a positive integer); " +
                $"got '{directory}' and '{text}'.");
        }
        return (directory, count);
    }

    /// <summary>Acquire a slot of the environment's shared limit; null (nothing to release) without one.</summary>
    internal static Task<IDisposable> AcquireAsync(CancellationToken token = default)
    {
        var settings = FromEnvironment();
        return settings == null ? Task.FromResult<IDisposable>(null) : AcquireAsync(settings.Value.Directory, settings.Value.Count, token);
    }

    /// <summary>Wait until one of <paramref name="count"/> slots in <paramref name="directory"/> is free and hold it until disposed.</summary>
    internal static async Task<IDisposable> AcquireAsync(string directory, int count, CancellationToken token = default)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "at least one slot");
        Directory.CreateDirectory(directory);
        while (true)
        {
            for (var slot = 0; slot < count; slot++)
            {
                var held = TryHold(Path.Combine(directory, $"slot-{slot}.lock"));
                if (held != null) return held;
            }
            await Task.Delay(Poll, token);
        }
    }

    private static FileStream TryHold(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
