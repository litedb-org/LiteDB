using System;
using System.Collections.Generic;
using LiteDB.ReproRunner.Cli.Manifests;

namespace LiteDB.ReproRunner.Cli.Execution;

internal sealed class ReproOutcomeEvaluator
{
    public ReproRunEvaluation Evaluate(ReproManifest manifest, ReproExecutionResult? packageResult, ReproExecutionResult? latestResult)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var packageExpectation = manifest.ExpectedOutcomes.Package ?? new ReproOutcomeExpectation(ReproOutcomeKind.Reproduce, null, null);
        var latestExpectation = manifest.ExpectedOutcomes.Latest ?? CreateDefaultLatestExpectation(manifest.State);

        var package = EvaluateVariant(packageExpectation, packageResult, isLatest: false, manifest.State);
        var latest = EvaluateVariant(latestExpectation, latestResult, isLatest: true, manifest.State);

        var shouldFail = package.ShouldFail || latest.ShouldFail;
        var shouldWarn = package.ShouldWarn || latest.ShouldWarn;

        return new ReproRunEvaluation(manifest.State, package, latest, shouldFail, shouldWarn);
    }

    private static ReproVariantEvaluation EvaluateVariant(
        ReproOutcomeExpectation expectation,
        ReproExecutionResult? result,
        bool isLatest,
        ReproState state)
    {
        var actualKind = ComputeActualKind(result);
        var (met, failureReason) = MatchesExpectation(expectation, result);

        var shouldFail = false;
        var shouldWarn = false;

        if (!met)
        {
            if (isLatest)
            {
                if (state == ReproState.Flaky)
                {
                    shouldWarn = true;
                }
                else
                {
                    shouldFail = true;
                }
            }
            else
            {
                shouldFail = true;
            }
        }

        return new ReproVariantEvaluation(expectation, actualKind, result, met, shouldFail, shouldWarn, failureReason);
    }

    private static (bool Met, string? FailureReason) MatchesExpectation(ReproOutcomeExpectation expectation, ReproExecutionResult? result)
    {
        if (result is null)
        {
            return (false, "Variant did not execute.");
        }

        var exitCode = result.Value.ExitCode;

        switch (expectation.Kind)
        {
            case ReproOutcomeKind.Reproduce:
                if (exitCode != 0)
                {
                    return (false, $"Expected exit code 0 but observed {exitCode}.");
                }
                break;
            case ReproOutcomeKind.NoRepro:
            case ReproOutcomeKind.HardFail:
                if (exitCode == 0)
                {
                    return (false, "Expected non-zero exit code.");
                }
                break;
            default:
                return (false, $"Unsupported expectation kind: {expectation.Kind}.");
        }

        if (expectation.ExitCode.HasValue && exitCode != expectation.ExitCode.Value)
        {
            return (false, $"Expected exit code {expectation.ExitCode.Value} but observed {exitCode}.");
        }

        if (!MatchesLog(expectation.LogContains, result.Value.CapturedOutput))
        {
            return (false, $"Expected output containing '{expectation.LogContains}'.");
        }

        return (true, null);
    }

    private static bool MatchesLog(string? expected, IReadOnlyList<ReproExecutionCapturedLine> lines)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        foreach (var line in lines)
        {
            if (line.Text?.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static ReproOutcomeKind ComputeActualKind(ReproExecutionResult? result)
    {
        if (result is null)
        {
            return ReproOutcomeKind.NoRepro;
        }

        return result.Value.ExitCode == 0
            ? ReproOutcomeKind.Reproduce
            : ReproOutcomeKind.NoRepro;
    }

    private static ReproOutcomeExpectation CreateDefaultLatestExpectation(ReproState state)
    {
        return state switch
        {
            ReproState.Red => new ReproOutcomeExpectation(ReproOutcomeKind.Reproduce, null, null),
            ReproState.Green => new ReproOutcomeExpectation(ReproOutcomeKind.NoRepro, null, null),
            ReproState.Flaky => new ReproOutcomeExpectation(ReproOutcomeKind.Reproduce, null, null),
            _ => new ReproOutcomeExpectation(ReproOutcomeKind.Reproduce, null, null)
        };
    }
}

internal sealed class ReproRunEvaluation
{
    public ReproRunEvaluation(ReproState state, ReproVariantEvaluation package, ReproVariantEvaluation latest, bool shouldFail, bool shouldWarn)
    {
        State = state;
        Package = package;
        Latest = latest;
        ShouldFail = shouldFail;
        ShouldWarn = shouldWarn;
    }

    public ReproState State { get; }

    public ReproVariantEvaluation Package { get; }

    public ReproVariantEvaluation Latest { get; }

    public bool ShouldFail { get; }

    public bool ShouldWarn { get; }
}

internal sealed class ReproVariantEvaluation
{
    public ReproVariantEvaluation(
        ReproOutcomeExpectation expectation,
        ReproOutcomeKind actualKind,
        ReproExecutionResult? result,
        bool met,
        bool shouldFail,
        bool shouldWarn,
        string? failureReason)
    {
        Expectation = expectation;
        ActualKind = actualKind;
        Result = result;
        Met = met;
        ShouldFail = shouldFail;
        ShouldWarn = shouldWarn;
        FailureReason = failureReason;
    }

    public ReproOutcomeExpectation Expectation { get; }

    public ReproOutcomeKind ActualKind { get; }

    public ReproExecutionResult? Result { get; }

    public bool Met { get; }

    public bool ShouldFail { get; }

    public bool ShouldWarn { get; }

    public string? FailureReason { get; }
}
