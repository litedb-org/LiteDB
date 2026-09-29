#if DEBUG || TESTING
using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// The stop guard after a recorded write or sync failure, at the layer before read-only
    /// continuation: the failure is recorded before the stop it causes (decision 6), the engine stops
    /// for good, and every later read and write on it throws the stop error that carries the original
    /// failure, without touching a file. (The continuation layer replaces the stop with a read-only
    /// reopen; its tests assert that instead.)
    /// </summary>
    internal static class TerminalStopAfterWriteFailure
    {
        /// <summary>
        /// <paramref name="engine"/> recorded <paramref name="operation"/> on <paramref name="file"/> (null:
        /// not checked) with an error starting <paramref name="error"/> and the log file kept
        /// (<paramref name="walKept"/>; null: not checked), and stopped.
        /// </summary>
        internal static void AssertRecorded(LiteEngine engine, string operation, string file, string error, bool? walKept = true)
        {
            var state = engine.GetState();
            var failure = state.WriteFailure;
            failure.Should().NotBeNull("the failure is recorded before the stop it causes");
            failure.Operation.Should().Be(operation);
            if (file != null) failure.File.Should().Be(file);
            failure.Error.Should().StartWith(error);
            if (walKept.HasValue) failure.WalKept.Should().Be(walKept.Value);
            state.Stopped.Should().BeTrue("the failure stopped the engine");
        }

        /// <summary>
        /// <paramref name="operation"/> (a read or a write) on the stopped engine throws its stop error,
        /// which carries the failure <paramref name="cause"/> (or one whose message starts with it).
        /// </summary>
        internal static IOException AssertRefused(Action operation, Exception cause) => AssertRefused(operation, cause.Message, cause);

        internal static IOException AssertRefused(Action operation, string error, Exception cause = null)
        {
            var refused = operation.Should().Throw<IOException>().Which;
            refused.Message.Should().StartWith("Engine closed after an I/O failure").And.Contain(error);
            if (cause != null) refused.InnerException.Should().BeSameAs(cause, "the stop carries the original failure");
            return refused;
        }
    }
}
#endif
