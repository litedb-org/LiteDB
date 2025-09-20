global using static LiteDB.Logging; // make LOG method available globally
using System;
using System.Diagnostics;

namespace LiteDB;

public static class Logging
{
    public static event Action<LogEventArgs> LogCallback;

    /// <summary>
    /// Log a message using LogCallback event
    /// </summary>
    [DebuggerHidden]
    internal static void LOG(string message, string category)
    {
        LogCallback?.Invoke(new LogEventArgs() { Message = message, Category = category });
    }

    /// <summary>
    /// Log a message using LogCallback event only if conditional = true
    /// </summary>
    [DebuggerHidden]
    internal static void LOG(bool conditional, string message, string category)
    {
        if (conditional) LOG(message, category);
    }

    /// <summary>
    /// Log an exception using LogCallback event only if conditional = true
    /// </summary>
    [DebuggerHidden]
    internal static void LOG(Exception exception, string category)
    {
        LogCallback?.Invoke(new LogEventArgs() { Exception = exception, Category = category });
    }
}

public class LogEventArgs
{
    public string Category { get; set; }
    public string Message { get; set; }
    public Exception Exception { get; set; }
}