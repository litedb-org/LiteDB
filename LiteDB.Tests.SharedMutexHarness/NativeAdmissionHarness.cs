using System.Reflection;
using LiteDB;
using LiteDB.Engine;

internal static class NativeAdmissionHarness
{
    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        if (!mode.StartsWith("native-", StringComparison.Ordinal)) return false;
        var settings = new EngineSettings { Filename = filename, Password = password };
        var shared = args[4].Contains("shared", StringComparison.Ordinal);
        settings.ReadOnly = args[4].Contains("readonly", StringComparison.Ordinal);
        if (mode == "native-rebuild-hold")
        {
            var stage = args[4].Split('|')[0];
            var rebuild = typeof(LiteEngine).Assembly.GetType("LiteDB.Engine.RebuildService")!;
            rebuild.GetField("SimulateInstallFailure", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, (Action<string>)(actual =>
                {
                    if (actual != stage) return;
                    Console.WriteLine("ready");
                    Console.ReadLine();
                }));
            using var db = new LiteDatabase(shared ? new SharedEngine(settings) : new LiteEngine(settings));
            db.Rebuild();
            Console.WriteLine("installed");
            Console.ReadLine();
            return true;
        }
        try
        {
            using var db = new LiteDatabase(shared ? new SharedEngine(settings) : new LiteEngine(settings));
            if (db.GetCollection("rows").FindById(1)?["value"].AsInt32 != 42)
                throw new Exception("Acknowledged record missing");
            if (mode == "native-rejected") throw new Exception("Incompatible engine was admitted");
            if (mode == "native-hold")
            {
                Console.WriteLine("ready");
                Console.ReadLine();
            }
            else Console.WriteLine("done");
        }
        catch (DatabaseAdmissionException) when (mode == "native-rejected") { Console.WriteLine("done"); }
        catch (LiteException e) when (mode == "native-rejected" && e.ErrorCode == LiteException.REBUILD_INCOMPLETE)
        { Console.WriteLine("done"); }
        return true;
    }
}
