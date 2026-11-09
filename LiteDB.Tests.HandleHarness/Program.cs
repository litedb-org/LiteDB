using System;
using System.IO;
using System.Linq;

internal static class Program
{
    private static void Main(string[] args)
    {
        Console.WriteLine("runtime:" + Environment.Version.Major + ":" + IntPtr.Size);
        if (!TransactionHandleHarness.TryRun(args[1], args[2], args[3] == "-" ? null : args[3], args))
            throw new InvalidOperationException(args[1]);
    }
}
