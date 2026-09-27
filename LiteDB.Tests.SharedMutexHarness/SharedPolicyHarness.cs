using System.Runtime.InteropServices;
using System.Reflection;
using LiteDB;
using LiteDB.Engine;

internal static class SharedPolicyHarness
{
    internal static bool TryRun(string mode, string filename, string? password)
    {
        if (mode == "mapped-optout")
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = filename, Password = password });
            using var database = new LiteDatabase(engine);
            for (var i = 0; i < 3; i++)
                if (database.GetCollection("docs").FindById(0)["value"].AsInt32 != 0) throw new Exception("Wrong row");
            if ((int)typeof(SharedEngine).GetField("CoordinatedReadHits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)! != 0 || !engine.CoordinationFallbackReason.Contains("LiteDB.DisableSharedMappedReads"))
                throw new Exception("Environment opt-out did not disable mapped admission");
            var row = database.GetCollection("docs").FindById(0);
            row["value"] = 7;
            database.GetCollection("docs").Update(row);
            Console.WriteLine("done");
            return true;
        }
        if (mode == "mapped-locking-disabled-conflict") AppContext.SetSwitch("System.IO.DisableFileLocking", false);
        else if (mode != "mapped-locking-disabled") return false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows sharing locks are mandatory and this Unix runtime knob is ignored.
            using var database = new LiteDatabase(new ConnectionString
                { Filename = filename, Password = password, Connection = ConnectionType.Shared });
            if (database.GetCollection("docs").FindById(0)["value"].AsInt32 != 0) throw new Exception("Wrong row");
        }
        else
        {
            try
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = filename, Password = password });
                throw new Exception("Shared mode accepted disabled file locking");
            }
            catch (PlatformNotSupportedException error) when (error.Message.Contains("file-sharing locks")) { }
        }
        Console.WriteLine("done");
        return true;
    }
}
