using System.Reflection;
using System.Runtime.ExceptionServices;
using LiteDB;

namespace Issue_1087_LinuxLockRetry;

/// <summary>
/// Invokes the equivalent internal lock helpers in the affected 4.1 package and current source.
/// Reflection is required because neither version exposes its I/O retry policy publicly.
/// </summary>
internal static class LiteDbRetryBridge
{
    private static readonly Assembly LiteDbAssembly = typeof(LiteDatabase).Assembly;

    public static bool IsLocked(IOException exception)
    {
        var extensionsType = LiteDbAssembly.GetType("LiteDB.IOExceptionExtensions", throwOnError: true)!;
        var method = extensionsType.GetMethod(
            "IsLocked",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(IOException) },
            modifiers: null) ?? throw new MissingMethodException(extensionsType.FullName, "IsLocked");

        return (bool)(method.Invoke(null, new object[] { exception })
            ?? throw new InvalidOperationException("IsLocked returned null."));
    }

    public static void VerifyControls()
    {
        var nonLock = new IOException("Issue 1087 non-lock control", unchecked((int)0x80070002));
        var recognizedLock = new IOException("Issue 1087 recognized-lock control", unchecked((int)0x80070020));
        var attempts = 0;
        Exception? observed = null;

        if (IsLocked(nonLock) || !IsLocked(recognizedLock))
        {
            throw new InvalidOperationException(
                "Classifier controls failed: code 2 must be rejected and Windows lock code 32 must be recognized.");
        }

        try
        {
            Invoke(() =>
            {
                attempts++;
                throw nonLock;
            }, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            observed = ex;
        }

        if (attempts != 1 || !ReferenceEquals(observed, nonLock))
        {
            throw new InvalidOperationException(
                $"Non-lock control was not propagated exactly once: attempts={attempts}, " +
                $"exception={observed?.GetType().FullName ?? "none"}.");
        }
    }

    public static void Invoke(Action action, TimeSpan timeout)
    {
        var helperType = LiteDbAssembly.GetType("LiteDB.FileHelper", throwOnError: true)!;

        var exec = helperType.GetMethod(
            "Exec",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(int), typeof(Action) },
            modifiers: null);

        var legacyTryExec = helperType.GetMethod(
            "TryExec",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(Action), typeof(TimeSpan) },
            modifiers: null);

        try
        {
            if (exec is not null)
            {
                exec.Invoke(null, new object[] { (int)Math.Ceiling(timeout.TotalSeconds), action });
                return;
            }

            if (legacyTryExec is not null)
            {
                legacyTryExec.Invoke(null, new object[] { action, timeout });
                return;
            }

            throw new MissingMethodException(
                helperType.FullName,
                "Exec(int, Action) or TryExec(Action, TimeSpan)");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
