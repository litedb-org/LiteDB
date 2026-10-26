using LiteDB.Tests.Engine;

internal static class AllocationHarness
{
    internal static bool TryRun(string mode, string filename, string password, string[] args)
    {
        if (mode != "allocation-publication") return false;
        var options = args[4].Split(' ');
        using var db = AllocationWorkload.Open(filename, password, int.Parse(options[1]));
        AllocationWorkload.Seed(db, options[0]);
        AllocationWorkload.Publish(db, options[0], stage: stage =>
        {
            if (stage != options[2]) return;
            Console.WriteLine("ready");
            Console.ReadLine(); // Parent sends SIGKILL here; no disposal or rollback.
        });
        return true;
    }
}
