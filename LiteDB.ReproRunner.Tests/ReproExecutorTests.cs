using System.Text.Json;
using LiteDB.ReproRunner.Cli.Execution;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace LiteDB.ReproRunner.Tests;

public sealed class ReproExecutorTests
{
    [Fact]
    public void LogObserver_ReceivesEntries_WhenSuppressed()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var executor = new ReproExecutor(output, error)
        {
            SuppressConsoleLogOutput = true
        };

        var observed = new List<ReproExecutionLogEntry>();
        executor.LogObserver = entry => observed.Add(entry);

        var envelope = ReproHostMessageEnvelope.CreateLog("critical failure", ReproHostLogLevel.Error);
        var json = JsonSerializer.Serialize(envelope, ReproJsonOptions.Default);

        var parsed = executor.TryProcessStructuredLine(json, 3);

        Assert.True(parsed);
        Assert.Single(observed);
        Assert.Equal(3, observed[0].InstanceIndex);
        Assert.Equal("critical failure", observed[0].Message);
        Assert.Equal(ReproHostLogLevel.Error, observed[0].Level);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }
}
