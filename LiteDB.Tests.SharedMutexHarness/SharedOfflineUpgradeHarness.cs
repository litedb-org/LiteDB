using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

internal static class SharedOfflineUpgradeHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode == "offline-legacy-hold")
        {
            using var legacy = new SharedEngine(new EngineSettings { Filename = filename });
            Console.WriteLine("ready");
            Console.ReadLine();
            Console.WriteLine("done");
            return true;
        }
        if (mode != "offline-upgrade-cut") return false;
        var count = 0;
        var cut = int.Parse(args[4]);
        var reached = false;
        SharedCoordinationFile.CreationStage = (_, stage) =>
        {
            if (!stage.StartsWith("offline-", StringComparison.Ordinal) || ++count != cut) return;
            reached = true;
            Console.WriteLine("ready");
            Console.ReadLine();
        };
        var settings = new EngineSettings { Filename = filename, SharedOneFile = true };
        using var admission = NativeAdmission.Open(settings, true);
        SharedOfflineUpgrade.Run(settings, admission);
        if (!reached && cut != 1) throw new Exception("Retirement boundary was not reached: " + cut);
        Console.WriteLine("ready");
        Console.ReadLine();
        return true;
    }
}
