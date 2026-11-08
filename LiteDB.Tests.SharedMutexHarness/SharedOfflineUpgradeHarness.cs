using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

internal static class SharedOfflineUpgradeHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode.StartsWith("default-family-", System.StringComparison.Ordinal))
        {
            var familySettings = new EngineSettings { Filename = filename };
            if (mode.Contains("legacy")) familySettings.SharedOneFile = false;
            var refused = mode.EndsWith("refused", System.StringComparison.Ordinal);
            try
            {
                using var engine = new SharedEngine(familySettings);
                if (refused) throw new System.Exception("Mixed family unexpectedly admitted");
                System.Console.WriteLine("ready");
                System.Console.ReadLine();
            }
            catch (System.IO.IOException error) when (refused)
            {
                if (!error.ToString().Contains("admission")) throw;
            }
            System.Console.WriteLine("done");
            return true;
        }
        if (mode == "offline-legacy-hold")
        {
            using var legacy = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = false });
            Console.WriteLine("ready");
            Console.ReadLine();
            Console.WriteLine("done");
            return true;
        }
        if (mode != "offline-upgrade-cut") return false;
        var count = 0;
        var boundary = args[4].Split(':');
        var cut = int.Parse(boundary[0]);
        var reached = false;
        SharedCoordinationFile.CreationStage = (_, stage) =>
        {
            if (!stage.StartsWith("offline-", StringComparison.Ordinal) || ++count != cut) return;
            if (stage != boundary[1]) throw new Exception("Wrong retirement stage: " + stage);
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
