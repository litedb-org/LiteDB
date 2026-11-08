namespace LiteDB.Fuzz.Targets;

internal sealed class LegacySharedProcessFuzzer : SharedProcessFuzzer
{
    internal LegacySharedProcessFuzzer() : base(legacy: true) { }
    public override string Name => "shared-legacy";
}
