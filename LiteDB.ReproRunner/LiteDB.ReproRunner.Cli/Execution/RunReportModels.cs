using System;
using System.Collections.Generic;
using LiteDB.ReproRunner.Cli.Manifests;

namespace LiteDB.ReproRunner.Cli.Execution;

internal sealed class RunReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string? Root { get; init; }

    public IReadOnlyList<RunReportEntry> Repros => _repros;

    private readonly List<RunReportEntry> _repros = new();

    public void Add(RunReportEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        _repros.Add(entry);
    }
}

internal sealed class RunReportEntry
{
    public string Id { get; init; } = string.Empty;

    public ReproState State { get; init; }

    public bool Failed { get; init; }

    public bool Warned { get; init; }

    public RunReportVariant Package { get; init; } = new();

    public RunReportVariant Latest { get; init; } = new();
}

internal sealed class RunReportVariant
{
    public ReproOutcomeKind Expected { get; init; }

    public int? ExpectedExitCode { get; init; }

    public string? ExpectedLogContains { get; init; }

    public ReproOutcomeKind Actual { get; init; }

    public bool Met { get; init; }

    public int? ExitCode { get; init; }

    public double? DurationSeconds { get; init; }

    public bool UseProjectReference { get; init; }

    public string? FailureReason { get; init; }

    public IReadOnlyList<RunReportCapturedLine> Output { get; init; } = Array.Empty<RunReportCapturedLine>();
}

internal sealed class RunReportCapturedLine
{
    public string Stream { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;
}
