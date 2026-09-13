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
