global using static LiteDB.Logging;

using System;
using System.Diagnostics;

namespace LiteDB
{
    /// <summary>
    /// Provides process-wide notifications for diagnostic messages emitted by LiteDB.
    /// </summary>
    /// <remarks>
    /// Subscriptions are global and remain active until explicitly removed. Subscribers are
    /// invoked synchronously on the thread that produced the message and may be invoked
    /// concurrently by different threads. Each message uses a snapshot of the invocation
    /// list. Exceptions from one subscriber are ignored and do not prevent later subscribers
    /// from running. Messages produced recursively by a subscriber on the same thread are
    /// suppressed, and subscriber failures are never reported through this event.
    /// </remarks>
    public static class Logging
    {
        [ThreadStatic]
        private static bool _isDispatching;

        /// <summary>
        /// Occurs when LiteDB emits a diagnostic message.
        /// </summary>
        /// <remarks>
        /// This event holds a strong reference to each subscriber. Applications should
        /// unsubscribe when the subscriber's lifetime ends.
        /// </remarks>
        public static event Action<LogEventArgs> LogCallback;

        /// <summary>
        /// Gets whether a message can be dispatched on the current thread.
        /// </summary>
        internal static bool IsEnabled => LogCallback != null && _isDispatching == false;

        /// <summary>
        /// Logs a text message to the current subscribers.
        /// </summary>
        [DebuggerHidden]
        internal static void LOG(string message, string category)
        {
            var subscribers = LogCallback;

            if (subscribers == null || _isDispatching) return;

            Dispatch(subscribers, new LogEventArgs(category, message, null));
        }

        /// <summary>
        /// Logs an exception to the current subscribers.
        /// </summary>
        [DebuggerHidden]
        internal static void LOG(Exception exception, string category)
        {
            var subscribers = LogCallback;

            if (subscribers == null || _isDispatching) return;

            Dispatch(subscribers, new LogEventArgs(category, null, exception));
        }

        private static void Dispatch(Action<LogEventArgs> subscribers, LogEventArgs args)
        {
            _isDispatching = true;

            try
            {
                foreach (Action<LogEventArgs> subscriber in subscribers.GetInvocationList())
                {
                    try
                    {
                        subscriber(args);
                    }
                    catch (Exception)
                    {
                        // Logging must never alter database control flow or hide its exception.
                    }
                }
            }
            finally
            {
                _isDispatching = false;
            }
        }
    }

    /// <summary>
    /// Contains one diagnostic message emitted by LiteDB.
    /// </summary>
    public sealed class LogEventArgs : EventArgs
    {
        internal LogEventArgs(string category, string message, Exception exception)
        {
            this.Category = category;
            this.Message = message;
            this.Exception = exception;
        }

        /// <summary>
        /// Gets the diagnostic category.
        /// </summary>
        public string Category { get; }

        /// <summary>
        /// Gets the diagnostic text, or null when <see cref="Exception"/> is set.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets the diagnostic exception, or null for a text message.
        /// </summary>
        public Exception Exception { get; }
    }
}
