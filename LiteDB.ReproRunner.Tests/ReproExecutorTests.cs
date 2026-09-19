using System;
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

    [Fact]
    public void TryProcessStructuredLine_RejectsMessagesBeforeConfiguration()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var executor = new ReproExecutor(output, error);
        executor.ConfigureExpectedConfiguration(false, "5.0.20", 1);

        var logEnvelope = ReproHostMessageEnvelope.CreateLog("hello", ReproHostLogLevel.Information);
        var json = JsonSerializer.Serialize(logEnvelope, ReproJsonOptions.Default);

        var parsed = executor.TryProcessStructuredLine(json, 0);

        Assert.True(parsed);
        Assert.Contains("configuration error", error.ToString());
        Assert.Contains("expected configuration handshake", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void TryProcessStructuredLine_AllowsMessagesAfterValidConfiguration()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var executor = new ReproExecutor(output, error);
        executor.ConfigureExpectedConfiguration(false, "5.0.20", 1);

        var configuration = ReproHostMessageEnvelope.CreateConfiguration(false, "5.0.20");
        var configurationJson = JsonSerializer.Serialize(configuration, ReproJsonOptions.Default);
        var logEnvelope = ReproHostMessageEnvelope.CreateLog("ready", ReproHostLogLevel.Information);
        var logJson = JsonSerializer.Serialize(logEnvelope, ReproJsonOptions.Default);

        var configParsed = executor.TryProcessStructuredLine(configurationJson, 0);
        var logParsed = executor.TryProcessStructuredLine(logJson, 0);

        Assert.True(configParsed);
        Assert.True(logParsed);
        Assert.Contains("ready", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void TryProcessStructuredLine_FlagsConfigurationMismatch()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var executor = new ReproExecutor(output, error);
        executor.ConfigureExpectedConfiguration(false, "5.0.20", 1);

        var configuration = ReproHostMessageEnvelope.CreateConfiguration(true, "5.0.20");
        var configurationJson = JsonSerializer.Serialize(configuration, ReproJsonOptions.Default);
        var parsed = executor.TryProcessStructuredLine(configurationJson, 0);

        Assert.True(parsed);
        Assert.Contains("configuration error", error.ToString());
        Assert.Contains("UseProjectReference=True", error.ToString(), StringComparison.OrdinalIgnoreCase);

        var logEnvelope = ReproHostMessageEnvelope.CreateLog("ignored", ReproHostLogLevel.Information);
        var logJson = JsonSerializer.Serialize(logEnvelope, ReproJsonOptions.Default);
        executor.TryProcessStructuredLine(logJson, 0);

        Assert.Equal(string.Empty, output.ToString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_or_missing_configuration_fails_independently_of_child_exit(bool missing)
    {
        var executor = new ReproExecutor(new StringWriter(), new StringWriter());
        executor.ConfigureExpectedConfiguration(false, "5.0.20", 1);
        if (!missing)
        {
            var invalid = ReproHostMessageEnvelope.CreateConfiguration(true, "5.0.20");
            executor.TryProcessStructuredLine(JsonSerializer.Serialize(invalid, ReproJsonOptions.Default), 0);
        }
        Assert.False(executor.ValidateConfiguration());
    }

    [Fact]
    public void Valid_configuration_is_reported_separately()
    {
        var executor = new ReproExecutor(new StringWriter(), new StringWriter());
        executor.ConfigureExpectedConfiguration(false, "5.0.20", 1);
        var valid = ReproHostMessageEnvelope.CreateConfiguration(false, "5.0.20");
        executor.TryProcessStructuredLine(JsonSerializer.Serialize(valid, ReproJsonOptions.Default), 0);
        Assert.True(executor.ValidateConfiguration());
    }

    [Theory]
    [InlineData(10, 20, 20)]
    [InlineData(20, 10, 20)]
    [InlineData(10, -2, -2)]
    [InlineData(10, 1, 1)]
    [InlineData(0, 20, 20)]
    [InlineData(10, 0, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(10, 10, 10)]
    [InlineData(0, 0, 0)]
    public void Multiple_instances_prioritize_errors_then_reproductions(int first, int second, int expected)
    {
        Assert.Equal(expected, ReproExecutor.AggregateExitCodes(new[] { first, second }));
    }

}
