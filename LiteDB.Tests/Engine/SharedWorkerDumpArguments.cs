using FluentAssertions;

namespace LiteDB.Tests.Engine;

internal static class SharedWorkerDumpArguments
{
    internal static void AssertCaptureCommand(string arguments, int processId)
    {
        // Match options only outside the quoted path. A PID or directory can
        // contain "-64" without requesting a WOW64 subsystem dump.
        var pattern = @"\A-accepteula -ma -r -at 5 " + processId +
            @" ""[^""\r\n]*[\\/]dump-" + processId + @"-[0-9a-f]{32}\.dmp""\z";
        arguments.Should().MatchRegex(pattern);
    }
}
