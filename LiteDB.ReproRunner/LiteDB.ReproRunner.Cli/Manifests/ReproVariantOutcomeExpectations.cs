namespace LiteDB.ReproRunner.Cli.Manifests;

internal sealed class ReproVariantOutcomeExpectations
{
    public static readonly ReproVariantOutcomeExpectations Empty = new(null, null);

    public ReproVariantOutcomeExpectations(ReproOutcomeExpectation? package, ReproOutcomeExpectation? latest)
    {
        Package = package;
        Latest = latest;
    }

    public ReproOutcomeExpectation? Package { get; }

    public ReproOutcomeExpectation? Latest { get; }
}
