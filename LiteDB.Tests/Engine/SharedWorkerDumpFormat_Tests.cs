using System;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine;

public partial class SharedWorkerDump_Tests
{
    [Theory]
    [InlineData(0, "complete", true)]
    [InlineData(1, "complete", true)]
    [InlineData(7, "complete", false)]
    [InlineData(0, "missing", false)]
    [InlineData(1, "missing", false)]
    [InlineData(1, "truncated", false)]
    [InlineData(1, "no-threads", false)]
    [InlineData(1, "no-modules", false)]
    [InlineData(1, "no-memory", false)]
    [InlineData(1, "wrong-pid", false)]
    [InlineData(1, "wrong-architecture", false)]
    [InlineData(1, "bad-context", false)]
    [InlineData(1, "bad-directory", false)]
    public void ToolExit_RequiresCompleteMatchingDump(int exitCode, string fixture, bool expected)
    {
        var capture = new SharedWorkerProcessDump("configured-procdump.exe", _directory, start: info =>
        {
            var firstQuote = info.Arguments.IndexOf('"');
            var path = info.Arguments.Substring(firstQuote + 1).TrimEnd('"');
            if (fixture != "missing") WriteDump(path, fixture);
            return Child(sleep: false, exitCode: exitCode, redirect: true);
        });
        capture.Capture().Should().Contain("Dump exit code: " + exitCode)
            .And.Contain("Dump validated full process structure: " + expected);
    }

    // Minimal synthetic MINIDUMP structures, not a debugger-readable process.
    // The real Windows smoke separately proves an actual capture can be opened
    // structurally with the same checks before deleting its large memory payload.
    internal static void WriteDump(string path, string fixture)
    {
        using var current = Process.GetCurrentProcess();
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        stream.SetLength(4096);
        writer.Write(0x504d444dU);
        writer.Write(0xa793U);
        writer.Write(5U);
        writer.Write(fixture == "bad-directory" ? 4090U : 32U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(2UL); // MiniDumpWithFullMemory
        DirectoryEntry(writer, fixture == "no-threads" ? 0U : 3U, 52, 128);
        DirectoryEntry(writer, fixture == "no-modules" ? 0U : 4U, 112, 256);
        DirectoryEntry(writer, fixture == "no-memory" ? 0U : 9U, 32, 512);
        DirectoryEntry(writer, 7, 56, 768);
        DirectoryEntry(writer, 15, 24, 896);
        stream.Position = 128;
        writer.Write(1U); // one thread
        stream.Position = 128 + 4 + 40;
        writer.Write(128U); // context size
        writer.Write(fixture == "bad-context" ? 4090U : 1024U);
        stream.Position = 256;
        writer.Write(1U); // one module
        stream.Position = 512;
        writer.Write(1UL); // one memory range
        writer.Write(2048UL); // memory data RVA
        writer.Write(0x100000UL); // virtual address
        writer.Write(2048UL); // memory data length, extends to EOF
        stream.Position = 768;
        writer.Write((ushort)(fixture == "wrong-architecture" ? 12 : IntPtr.Size == 4 ? 0 : 9));
        stream.Position = 896;
        writer.Write(24U);
        writer.Write(1U); // MINIDUMP_MISC1_PROCESS_ID
        writer.Write((uint)(fixture == "wrong-pid" ? current.Id + 1 : current.Id));
        if (fixture == "truncated") stream.SetLength(4095);
    }

    private static void DirectoryEntry(BinaryWriter writer, uint type, uint size, uint rva)
    {
        writer.Write(type);
        writer.Write(size);
        writer.Write(rva);
    }
}
