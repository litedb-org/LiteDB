using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;

using LiteDB;
using LiteDB.Tests.Issues;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 4 && args[0] == "--hold-backup")
            {
                BackupLock.Hold(args[1], args[2], args[3]);
                return 0;
            }
            var target = typeof(Program).Assembly.GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName;
            var libraryTarget = typeof(LiteDatabase).Assembly.GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName;
            var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var source = bool.Parse(typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(item => item.Key == "LiteDB.ReproRunner.UseProjectReference").Value);
            var expectedLibraryTarget = source
                ? (windows ? ".NETStandard,Version=v2.0" : ".NETCoreApp,Version=v8.0")
                : (windows ? ".NETFramework,Version=v4.5" : ".NETStandard,Version=v2.0");
            if (libraryTarget != expectedLibraryTarget)
                throw new InvalidOperationException("The selected variant loaded an unexpected LiteDB assembly target: " + libraryTarget);
            if (windows && (Environment.Is64BitProcess || target != ".NETFramework,Version=v4.8"))
                throw new InvalidOperationException("Windows must exercise the 32-bit .NET Framework worker.");
            Console.WriteLine($"RUNTIME_2059: target={target}, bits={IntPtr.Size * 8}, " +
                $"clr={Environment.Version}, libraryTarget={libraryTarget}");
            new Issue2059_Tests().VerifyLookupLifecycle(4096, 8, BackupLock.Verify);
            Console.WriteLine("VERIFIED_WORKER_2059: complete ledger, indexed queries, backup lock, recovery write, read-only reopen");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 20;
        }
    }
}
