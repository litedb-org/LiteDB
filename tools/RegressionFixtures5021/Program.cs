using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// Regenerates the LiteDB 5.0.21 regression fixtures that LiteDB.Tests/Resources/RegressionFixtures.md
// describes, with the released package. Run from the repository root:
//   dotnet run --project tools/RegressionFixtures5021 -- <fixture> <empty-output-directory>
// The directory receives exactly the entries of that fixture's ZIP. Header creation times and
// encrypted bytes differ on every run; the manifest lists what must match instead.
internal static class Program
{
    internal const string ChildMarker = "--child";

    private static readonly Dictionary<string, Action<string>> Fixtures = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["WalCrash"] = CrashImages.WalCrash,
        ["EncryptedWalCrash"] = CrashImages.EncryptedWalCrash,
        ["ConcurrentWalCrash"] = CrashImages.ConcurrentWalCrash,
        ["ForeignWal"] = CrashImages.ForeignWal,
    };

    private static int Main(string[] args)
    {
        // The originals ran with LANG unset: 5.0.21 stored the invariant culture's collation
        // (LCID 127, IgnoreCase). Pin it so any host writes the same collation.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length == 3 && args[0] == ChildMarker)
            return CrashImages.Child(args[1], args[2]);

        var name = args.Length == 2 ? Path.GetFileNameWithoutExtension(args[0]) : null;
        if (name != null && name.EndsWith("_5_0_21", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 7);
        if (name == null || !Fixtures.TryGetValue(name, out var generate))
        {
            Console.Error.WriteLine("usage: RegressionFixtures5021 <fixture> <empty-output-directory>");
            Console.Error.WriteLine("fixtures: " + string.Join(", ", Fixtures.Keys));
            return 2;
        }

        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        if (Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("output directory is not empty: " + output);
            return 2;
        }

        Console.WriteLine($"LiteDB {typeof(LiteDB.LiteDatabase).Assembly.GetName().Version} on .NET {Environment.Version} " +
            $"({System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        generate(output);
        foreach (var file in Directory.GetFiles(output).OrderBy(x => x, StringComparer.Ordinal))
            Console.WriteLine($"{Path.GetFileName(file)} {new FileInfo(file).Length} sha256={Sha256(file)}");
        return 0;
    }

    internal static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>A scratch directory for files that are not part of the fixture.</summary>
    internal static string WorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "regression-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void DeleteWorkDirectory(string path)
    {
        try { Directory.Delete(path, true); }
        catch (IOException ex) { Console.Error.WriteLine("could not delete " + path + ": " + ex.Message); }
    }

    /// <summary>
    /// Run one step in a child process of this program, as the originals ran separate processes.
    /// This also starts 5.0.21's fixed-seed Randomizer (skip-list node levels) afresh, as they did.
    /// </summary>
    internal static void RunChild(string mode, string path, bool crashes = false)
    {
        var (exitCode, stdout, stderr) = StartChild(mode, path);
        Console.Write(stdout);
        if (crashes ? exitCode == 0 : exitCode != 0)
            throw new InvalidOperationException($"child {mode} exited with {exitCode}: {stderr}");
        if (crashes) Console.WriteLine($"child {mode} killed itself (exit {exitCode})");
    }

    private static (int exitCode, string stdout, string stderr) StartChild(string mode, string path)
    {
        var host = Environment.ProcessPath ?? "dotnet";
        var psi = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Under "dotnet RegressionFixtures5021.dll" the host is the muxer; under the apphost it is the program.
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        psi.ArgumentList.Add(ChildMarker);
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(path);
        // FailFast must not leave a crash dump beside the fixture files.
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + host);
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr.Result);
    }

    /// <summary>BSON bytes of a string element: type 0x02, the name, its int32 length and the value.</summary>
    internal static byte[] BsonString(string name, string value) =>
        new byte[] { 0x02 }.Concat(Encoding.UTF8.GetBytes(name + "\0"))
            .Concat(BitConverter.GetBytes(Encoding.UTF8.GetByteCount(value) + 1))
            .Concat(Encoding.UTF8.GetBytes(value + "\0")).ToArray();

    /// <summary>BSON bytes of an int32 element: type 0x10, the name and the value.</summary>
    internal static byte[] BsonInt32(string name, int value) =>
        new byte[] { 0x10 }.Concat(Encoding.UTF8.GetBytes(name + "\0")).Concat(BitConverter.GetBytes(value)).ToArray();

    internal static int[] FindAll(byte[] data, byte[] pattern)
    {
        var hits = new List<int>();
        for (var i = 0; i <= data.Length - pattern.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) hits.Add(i);
        return hits.ToArray();
    }

    internal static int FindUnique(byte[] data, byte[] pattern)
    {
        var hits = FindAll(data, pattern);
        if (hits.Length != 1)
            throw new InvalidOperationException($"expected one occurrence of {Convert.ToHexString(pattern)}, found {hits.Length}");
        return hits[0];
    }

    /// <summary>Overwrite the int32 at an offset (a BSON length or value).</summary>
    internal static void WriteInt32(byte[] data, int offset, int value) => BitConverter.GetBytes(value).CopyTo(data, offset);
}
