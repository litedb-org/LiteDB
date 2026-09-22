using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine;

public partial class SharedWorkerDump_Tests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable("LITEDB_SHARED_DIAGNOSTICS") ?? Path.GetTempPath(),
        "dump-contract-" + Guid.NewGuid().ToString("N"));
    private bool _retain;

    [Fact]
    public void ConcurrentTimeouts_WaitForOneCaptureBeforeReturningToCancellation()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var firstCancelled = new ManualResetEventSlim();
        using var secondCancelled = new ManualResetEventSlim();
        var calls = 0;
        var diagnostics = new SharedWorkerDiagnostics(_directory, true, () =>
        {
            Interlocked.Increment(ref calls);
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test capture gate");
            return "capture completed";
        });
        Exception firstError = null;
        Exception secondError = null;
        var first = new Thread(() =>
        {
            try { diagnostics.Timeout("first").Should().Contain("capture completed"); }
            catch (Exception error) { firstError = error; }
            finally { firstCancelled.Set(); }
        }) { IsBackground = true };
        var second = new Thread(() =>
        {
            secondStarted.Set();
            try { diagnostics.Timeout("second").Should().Contain("capture completed"); }
            catch (Exception error) { secondError = error; }
            finally { secondCancelled.Set(); }
        }) { IsBackground = true };
        first.Start();
        var secondWasStarted = false;
        try
        {
            started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            second.Start();
            secondWasStarted = true;
            secondStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            secondCancelled.Wait(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
            firstCancelled.IsSet.Should().BeFalse();
            calls.Should().Be(1);
        }
        finally
        {
            release.Set();
            first.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            if (secondWasStarted) second.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
        }
        firstError.Should().BeNull();
        secondError.Should().BeNull();
        firstCancelled.IsSet.Should().BeTrue();
        secondCancelled.IsSet.Should().BeTrue();
        calls.Should().Be(1);
    }

    [Fact]
    public void FailedCapture_DoesNotReplaceWorkerTimeoutEvidence()
    {
        var calls = 0;
        var diagnostics = new SharedWorkerDiagnostics(_directory, true, () =>
        {
            calls++;
            throw new IOException("injected dump failure");
        });
        diagnostics.Track("writer").Progress("inserting", 17);
        diagnostics.Timeout("writer").Should().Contain("completed=17").And.Contain("injected dump failure");
        diagnostics.Timeout("other writer").Should().Contain("injected dump failure");
        diagnostics.RetainFiles.Should().BeTrue();
        calls.Should().Be(1);
    }

    [Fact]
    public void ExpectedTempVolume_AcceptsSameDriveAndRejectsWorkspaceDrive()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
        CrossProcess_Shared_Tests.RequireTempVolume(@"C:\capture\database.db", @"c:\Users\runneradmin\AppData\Local\Temp\");
        Action differentDrive = () => CrossProcess_Shared_Tests.RequireTempVolume(
            @"D:\a\LiteDB\database.db", @"C:\Users\runneradmin\AppData\Local\Temp\");
        differentDrive.Should().Throw<Xunit.Sdk.TrueException>().WithMessage("*must match Path.GetTempPath volume*");
    }

    [Fact]
    public void MissingTool_IsReportedWithoutThrowingOrCreatingAFalseDump()
    {
        var capture = new SharedWorkerProcessDump(Path.Combine(_directory, "missing-procdump.exe"), _directory);
        capture.Capture().Should().Contain("Dump capture failed:");
        Directory.GetFiles(_directory, "*.dmp").Should().BeEmpty();
        Directory.GetFiles(_directory, "dump-*.txt").Should().ContainSingle();
    }

    [Fact]
    public void FailedToolExit_IsReportedAndDoesNotCertifyCapture()
    {
        ProcessStartInfo requested = null;
        var capture = new SharedWorkerProcessDump("configured-procdump.exe", _directory, start: info =>
        {
            requested = info;
            return Child(sleep: false, exitCode: 7, redirect: true);
        });
        var report = capture.Capture();
        report.Should().Contain("Dump exit code: 7").And.Contain("Dump validated full process structure: False");
        requested.FileName.Should().Be("configured-procdump.exe");
        requested.StandardOutputEncoding.Should().Be(System.Text.Encoding.Unicode);
        requested.StandardErrorEncoding.Should().Be(System.Text.Encoding.Unicode);
        using var current = Process.GetCurrentProcess();
        SharedWorkerDumpArguments.AssertCaptureCommand(requested.Arguments, current.Id);
    }

    [Fact]
    public void StalledTool_UsesBoundedCancellationAndLeavesTesthostAlive()
    {
        var cancelCalled = false;
        var childPid = 0;
        var capture = new SharedWorkerProcessDump("configured-procdump.exe", _directory, 1000, info =>
        {
            if (info.Arguments.StartsWith("-cancel ", StringComparison.Ordinal))
            {
                cancelCalled = true;
                return Child(sleep: false, exitCode: 0, redirect: false);
            }
            var child = Child(sleep: true, exitCode: 0, redirect: true);
            childPid = child.Id;
            return child;
        });
        var elapsed = Stopwatch.StartNew();
        capture.Capture().Should().Contain("Dump process timed out");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        cancelCalled.Should().BeTrue();
        Action findChild = () => { using var child = Process.GetProcessById(childPid); };
        findChild.Should().Throw<ArgumentException>("only the diagnostic child should have been terminated");
    }

    [Fact]
    public void ConfiguredWindowsCapture_ProducesAnActualDumpBeforeTimeoutReturns()
    {
        if (Environment.GetEnvironmentVariable("LITEDB_SHARED_DUMP_SMOKE") != "1") return;
        Environment.OSVersion.Platform.Should().Be(PlatformID.Win32NT);
        _retain = true;
        var report = SharedWorkerDumpSmoke.Capture(_directory, SharedWorkerProcessDump.FromEnvironment);
        report.Should().Contain("Dump exit code:").And.Contain("Dump validated full process structure: True");
        report.Should().Contain("processBits=" + IntPtr.Size * 8);
    }

    private static Process Child(bool sleep, int exitCode, bool redirect)
    {
        var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        return Process.Start(new ProcessStartInfo
        {
            FileName = windows ? (sleep ? "powershell.exe" : "cmd.exe") : (sleep ? "/bin/sleep" : "/bin/sh"),
            Arguments = windows
                ? (sleep ? "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"" : "/c exit " + exitCode)
                : (sleep ? "30" : "-c \"exit " + exitCode + "\""),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect
        });
    }

    public void Dispose()
    {
        if (!_retain && Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
