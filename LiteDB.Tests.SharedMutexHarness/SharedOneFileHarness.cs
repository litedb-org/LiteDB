using LiteDB.Client.Shared;

internal static class SharedOneFileHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode != "one-file-lock-hold") return false;
        using var locks = new SharedRangeLocks(filename);
        if (!locks.TryExclusiveDms()) throw new Exception("DMS busy");
        locks.DowngradeDms();
        if (!locks.TryClaim(0)) throw new Exception("Slot busy");
        locks.EnterWrite();
        Console.WriteLine("ready");
        Console.ReadLine();
        Console.WriteLine("done");
        return true;
    }
}
