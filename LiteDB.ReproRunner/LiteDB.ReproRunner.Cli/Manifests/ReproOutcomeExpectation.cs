namespace LiteDB.ReproRunner.Cli.Manifests;

internal sealed class ReproOutcomeExpectation
{
    public ReproOutcomeExpectation(ReproOutcomeKind kind, int? exitCode, string? logContains)
    {
        Kind = kind;
        ExitCode = exitCode;
        LogContains = logContains;
    }

    public ReproOutcomeKind Kind { get; }

    public int? ExitCode { get; }

    public string? LogContains { get; }
}
