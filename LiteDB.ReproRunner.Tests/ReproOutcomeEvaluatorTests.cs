using System;
using LiteDB.ReproRunner.Cli.Execution;
using LiteDB.ReproRunner.Cli.Manifests;

namespace LiteDB.ReproRunner.Tests;

public sealed class ReproOutcomeEvaluatorTests
{
    private static readonly ReproOutcomeEvaluator Evaluator = new();

    [Fact]
    public void Evaluate_AllowsRedReproWhenLatestStillFails()
    {
        var manifest = CreateManifest(ReproState.Red);
        var packageResult = CreateResult(useProjectReference: false, exitCode: 0);
        var latestResult = CreateResult(useProjectReference: true, exitCode: 0);

        var evaluation = Evaluator.Evaluate(manifest, packageResult, latestResult);

        Assert.False(evaluation.ShouldFail);
        Assert.True(evaluation.Package.Met);
        Assert.True(evaluation.Latest.Met);
    }

    [Fact]
    public void Evaluate_FailsGreenWhenLatestStillReproduces()
    {
        var manifest = CreateManifest(ReproState.Green);
        var packageResult = CreateResult(false, 0);
        var latestResult = CreateResult(true, 0);

        var evaluation = Evaluator.Evaluate(manifest, packageResult, latestResult);

        Assert.True(evaluation.ShouldFail);
        Assert.True(evaluation.Package.Met);
        Assert.False(evaluation.Latest.Met);
    }

    [Fact]
    public void Evaluate_RespectsHardFailExpectationWhenLogMatches()
    {
        var expectation = new ReproVariantOutcomeExpectations(
            new ReproOutcomeExpectation(ReproOutcomeKind.HardFail, -5, "NetworkException"),
            null);
        var manifest = CreateManifest(ReproState.Green, expectation);
        var packageResult = CreateResult(false, -5, "NetworkException at socket");

        var evaluation = Evaluator.Evaluate(manifest, packageResult, null);

        Assert.False(evaluation.Package.ShouldFail);
        Assert.True(evaluation.Package.Met);
        Assert.Equal(ReproOutcomeKind.HardFail, evaluation.Package.Expectation.Kind);
    }

    [Fact]
    public void Evaluate_FailsHardFailWhenLogMissing()
    {
        var expectation = new ReproVariantOutcomeExpectations(
            new ReproOutcomeExpectation(ReproOutcomeKind.HardFail, -5, "NetworkException"),
            null);
        var manifest = CreateManifest(ReproState.Green, expectation);
        var packageResult = CreateResult(false, -5, "No matching text");

        var evaluation = Evaluator.Evaluate(manifest, packageResult, null);

        Assert.True(evaluation.Package.ShouldFail);
        Assert.False(evaluation.Package.Met);
        Assert.Contains("NetworkException", evaluation.Package.FailureReason);
    }

    [Fact]
    public void Evaluate_WarnsFlakyLatestMismatch()
    {
        var manifest = CreateManifest(ReproState.Flaky);
        var packageResult = CreateResult(false, 0);
        var latestResult = CreateResult(true, 1);

        var evaluation = Evaluator.Evaluate(manifest, packageResult, latestResult);

        Assert.False(evaluation.ShouldFail);
        Assert.True(evaluation.ShouldWarn);
        Assert.True(evaluation.Package.Met);
        Assert.False(evaluation.Latest.Met);
    }

    [Theory]
    [InlineData(0, "REPEATS_2814:", false)]
    [InlineData(10, "REPEATS_2814:", false)]
    [InlineData(20, "REPEATS_2814:", true)]
    [InlineData(-2, "REPEATS_2814:", true)]
    [InlineData(1, "REPEATS_2814:", true)]
    [InlineData(10, "wrong output", true)]
    public void Intermittent_package_accepts_only_completed_observations(int exitCode, string output, bool fails)
    {
        var expectation = new ReproVariantOutcomeExpectations(
            new ReproOutcomeExpectation(ReproOutcomeKind.Intermittent, null, "REPEATS_2814:"), null);
        var manifest = CreateManifest(ReproState.Green, expectation);
        var evaluation = Evaluator.Evaluate(manifest, CreateResult(false, exitCode, output), CreateResult(true, 10));
        Assert.Equal(fails, evaluation.ShouldFail);
        Assert.True(evaluation.Latest.Met);
        var recurrence = Evaluator.Evaluate(manifest, CreateResult(false, exitCode, output), CreateResult(true, 0));
        Assert.True(recurrence.ShouldFail);
    }

    [Theory]
    [InlineData(10, "✅")]
    [InlineData(0, "❌")]
    public void Fixed_result_cell_reflects_the_declared_expectation(int exitCode, string symbol)
    {
        var evaluation = Evaluator.Evaluate(CreateManifest(ReproState.Green), CreateResult(false, 0), CreateResult(true, exitCode));
        Assert.Contains(symbol, LiteDB.ReproRunner.Cli.Commands.RunCommand.FormatVariantCell(evaluation.Latest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Handshake_failure_fails_implicit_green_expectation(bool sendWrongConfiguration)
    {
        var executor = new ReproExecutor(new StringWriter(), new StringWriter());
        executor.ConfigureExpectedConfiguration(true, "5.0.20", 1);
        if (sendWrongConfiguration)
        {
            var wrong = LiteDB.ReproRunner.Shared.Messaging.ReproHostMessageEnvelope.CreateConfiguration(false, "5.0.20");
            executor.TryProcessStructuredLine(System.Text.Json.JsonSerializer.Serialize(wrong,
                LiteDB.ReproRunner.Shared.Messaging.ReproJsonOptions.Default), 0);
        }
        var result = CreateResult(true, 10) with { ConfigurationValid = executor.ValidateConfiguration() };
        var evaluation = Evaluator.Evaluate(CreateManifest(ReproState.Green), CreateResult(false, 0), result);
        Assert.True(evaluation.ShouldFail);
        Assert.False(evaluation.Latest.Met);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Negative_two_child_exit_is_valid_when_declared_and_handshake_passes(bool valid, bool fails)
    {
        var expectation = new ReproVariantOutcomeExpectations(
            new ReproOutcomeExpectation(ReproOutcomeKind.HardFail, -2, "expected crash"), null);
        var package = CreateResult(false, -2, "expected crash") with { ConfigurationValid = valid };
        var evaluation = Evaluator.Evaluate(CreateManifest(ReproState.Green, expectation), package, CreateResult(true, 10));
        Assert.Equal(fails, evaluation.ShouldFail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Intermittent_rejects_invalid_handshake_even_with_completed_summary(int exit)
    {
        var expectation = new ReproVariantOutcomeExpectations(
            new ReproOutcomeExpectation(ReproOutcomeKind.Intermittent, null, "REPEATS_2814:"), null);
        var package = CreateResult(false, exit, "REPEATS_2814:") with { ConfigurationValid = false };
        var evaluation = Evaluator.Evaluate(CreateManifest(ReproState.Green, expectation), package, CreateResult(true, 10));
        Assert.True(evaluation.ShouldFail);
    }

    private static ReproManifest CreateManifest(ReproState state, ReproVariantOutcomeExpectations? expectations = null)
    {
        return new ReproManifest(
            "Issue_Example",
            "Example",
            Array.Empty<string>(),
            null,
            120,
            false,
            1,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            state,
            expectations ?? ReproVariantOutcomeExpectations.Empty);
    }

    private static ReproExecutionResult CreateResult(bool useProjectReference, int exitCode, string? output = null)
    {
        var captured = output is null
            ? Array.Empty<ReproExecutionCapturedLine>()
            : new[]
            {
                new ReproExecutionCapturedLine(ReproExecutionStream.StandardOutput, output)
            };

        return new ReproExecutionResult(
            useProjectReference,
            exitCode == 0,
            exitCode,
            TimeSpan.FromSeconds(1),
            captured);
    }
}
