using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

using FluentAssertions;

using LiteDB;
using LiteDB.Tests.Issues;

internal static class BackupLock
{
    internal static void Hold(string path, string ready, string release)
    {
        using (var backup = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            backup.Length.Should().BeGreaterThan(15L * 1024 * 1024);
            File.WriteAllText(ready, "exclusive read handle acquired");
            SpinWait.SpinUntil(() => File.Exists(release), TimeSpan.FromSeconds(60))
                .Should().BeTrue("the parent must release the bounded backup handle");
        }
    }

    internal static void Verify(string path)
    {
        var ready = path + ".backup-ready";
        var release = path + ".backup-release";
        var before = Hash(path);
        var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        var assembly = typeof(BackupLock).Assembly.Location;
        var start = new ProcessStartInfo(windows ? assembly : "dotnet")
        {
            Arguments = (windows ? "" : Quote(assembly) + " ") +
                "--hold-backup " + Quote(path) + " " + Quote(ready) + " " + Quote(release),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (var child = Process.Start(start))
        {
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                SpinWait.SpinUntil(() => File.Exists(ready) || child.HasExited, TimeSpan.FromSeconds(15))
                    .Should().BeTrue("the backup process must reach its lock barrier");
                File.Exists(ready).Should().BeTrue("the child must actually hold the database handle");
                child.HasExited.Should().BeFalse();
                var error = Xunit.Record.Exception(() =>
                {
                    using (var database = new LiteDatabase(path))
                    {
                        database.GetCollection<Issue2059_Tests.SimpleRecord>("SimpleRecord").Query()
                            .Where(row => row.InvNum == "xp148jm576/43500001/2021/dk499zp719" &&
                                row.Zastareo == false && row.CisError == false).ToList();
                    }
                });
                error.Should().BeOfType<IOException>("the real backup handle must deny the read/open attempt");
                var code = error.HResult & 0xffff;
                if (windows) code.Should().Be(32, "Windows must report ERROR_SHARING_VIOLATION");
                else code.Should().Be(11, "the Unix exclusive lock must report EAGAIN");
                Console.WriteLine($"BACKUP_LOCK_2059: childHeldHandle=true, exception=IOException, nativeCode={code}");
            }
            finally
            {
                File.WriteAllText(release, "release");
                if (!child.WaitForExit(15000))
                {
                    child.Kill();
                    child.WaitForExit();
                }
                Console.Write(output.GetAwaiter().GetResult());
                Console.Error.Write(errors.GetAwaiter().GetResult());
                File.Delete(ready);
                File.Delete(release);
            }
            child.ExitCode.Should().Be(0);
        }
        Hash(path).Should().Equal(before, "the denied read and backup process must leave the data file intact");
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    private static byte[] Hash(string path)
    {
        using (var file = File.OpenRead(path))
        using (var hash = SHA256.Create()) return hash.ComputeHash(file);
    }
}
