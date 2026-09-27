using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine;

public class SharedWorkerDumpSmoke_Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-dump-smoke-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Smoke_AllThreeInvalidAttemptsFailAndRetainEveryPayloadAndReport()
    {
        var directories = new List<string>();
        Action run = () => SharedWorkerDumpSmoke.Capture(_directory, directory =>
        {
            directories.Add(directory);
            return () => SmokeFixture(directory, "truncated", reportValid: false);
        });

        run.Should().Throw<InvalidOperationException>().WithMessage("All three dump smoke attempts were invalid.*");
        directories.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        foreach (var directory in directories)
        {
            File.Exists(Path.Combine(directory, "fixture.dmp")).Should().BeTrue();
            File.ReadAllText(Path.Combine(directory, "smoke-capture-report.txt"))
                .Should().Contain("Dump validated full process structure: False;");
            Directory.GetFiles(directory, "timeout-*.txt").Should().ContainSingle();
        }
        var summary = File.ReadAllText(Path.Combine(_directory, "smoke-summary.txt"));
        foreach (var attempt in Enumerable.Range(1, 3)) summary.Should().Contain($"Smoke attempt {attempt}/3: valid=False;");
        File.Exists(Path.Combine(_directory, "successful-smoke.txt")).Should().BeFalse();
    }

    [Fact]
    public void Smoke_InvalidThenValidUsesFreshCaptureAndRetainsFirstAttempt()
    {
        var directories = new List<string>();
        var calls = new int[2];
        var summary = SharedWorkerDumpSmoke.Capture(_directory, directory =>
        {
            var attempt = directories.Count;
            directories.Add(directory);
            return () =>
            {
                calls[attempt]++;
                return SmokeFixture(directory, attempt == 0 ? "truncated" : "complete", reportValid: true);
            };
        });

        directories.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        calls.Should().Equal(1, 1);
        File.Exists(Path.Combine(directories[0], "fixture.dmp")).Should().BeTrue();
        File.Exists(Path.Combine(directories[1], "fixture.dmp")).Should().BeFalse();
        foreach (var directory in directories)
        {
            File.Exists(Path.Combine(directory, "smoke-capture-report.txt")).Should().BeTrue();
            Directory.GetFiles(directory, "timeout-*.txt").Should().ContainSingle();
        }
        summary.Should().Contain("Smoke attempt 1/3: valid=False;").And.Contain("Smoke attempt 2/3: valid=True;");
        File.ReadAllText(Path.Combine(_directory, "successful-smoke.txt")).Should().Be(summary);
    }

    [Fact]
    public void Smoke_FirstValidStopsImmediatelyAndDeletesOnlyValidatedDump()
    {
        Directory.CreateDirectory(_directory);
        var unrelated = Path.Combine(_directory, "unrelated.dmp");
        File.WriteAllText(unrelated, "preserve unrelated evidence");
        var calls = 0;
        string attemptDirectory = null;
        SharedWorkerDumpSmoke.Capture(_directory, directory =>
        {
            calls++;
            attemptDirectory = directory;
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "capture evidence");
            return () => SmokeFixture(directory, "complete", reportValid: true);
        });

        calls.Should().Be(1);
        File.Exists(Path.Combine(attemptDirectory, "fixture.dmp")).Should().BeFalse();
        File.ReadAllText(Path.Combine(attemptDirectory, "keep.txt")).Should().Be("capture evidence");
        File.ReadAllText(unrelated).Should().Be("preserve unrelated evidence");
    }

    [Theory]
    [InlineData("wrong-pid", true)]
    [InlineData("wrong-architecture", true)]
    [InlineData("truncated", true)]
    [InlineData("missing", true)]
    [InlineData("complete", false)]
    public void Smoke_NeedsMatchingStructureAndAnExplicitValidatedReport(string fixture, bool reportValid)
    {
        var calls = 0;
        Action run = () => SharedWorkerDumpSmoke.Capture(_directory, directory =>
        {
            calls++;
            return () => SmokeFixture(directory, fixture, reportValid);
        });

        run.Should().Throw<InvalidOperationException>().WithMessage("All three dump smoke attempts were invalid.*");
        calls.Should().Be(3);
        Directory.GetFiles(_directory, "*.dmp", SearchOption.AllDirectories).Should().HaveCount(fixture == "missing" ? 0 : 3);
        File.Exists(Path.Combine(_directory, "successful-smoke.txt")).Should().BeFalse();
    }

    [Fact]
    public void Smoke_UnexpectedFactoryFailurePropagatesWithoutRetry()
    {
        var error = new InvalidOperationException("unexpected factory failure");
        var calls = 0;
        Action run = () => SharedWorkerDumpSmoke.Capture(_directory, directory =>
        {
            calls++;
            throw error;
        });

        run.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(error);
        calls.Should().Be(1);
    }

    private static string SmokeFixture(string directory, string fixture, bool reportValid)
    {
        var dump = Path.Combine(directory, "fixture.dmp");
        if (fixture != "missing") SharedWorkerDump_Tests.WriteDump(dump, fixture);
        return $"Dump exit code: 1\nDump validated full process structure: {reportValid}; path={dump}";
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
