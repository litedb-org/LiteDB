using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2904_Tests
    {
        [Fact]
        public void Windows_architecture_matrix_passes_its_architecture_to_both_test_hosts()
        {
            var workflow = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(), ".github", "workflows", "_reusable-ci.yml"));

            using var scope = new AssertionScope();
            ExtractStep(workflow, "- name: Run tests (${{ matrix.arch }})")
                .Should().Contain("--arch ${{ matrix.arch }}");
            ExtractStep(workflow, "- name: Run cross-process shared mode tests (${{ matrix.arch }})")
                .Should().Contain("--arch ${{ matrix.arch }}");
            foreach (var job in new[] { "test-windows", "test-windows-crossprocess" })
            {
                var body = ExtractJob(workflow, job);
                body.Should().Contain("LITEDB_TEST_ARCH: ${{ matrix.arch }}");
                body.Should().Contain("if: matrix.arch == 'x86'");
                body.Should().Contain("install-x86-test-runtime.ps1 -Framework '${{ matrix.item.framework }}'");
                ExtractStep(body, "- name: Build test project").Should()
                    .Contain("--arch ${{ matrix.arch }} -p:AppendRuntimeIdentifierToOutputPath=false")
                    .And.Contain("-p:TestingEnabled=true");
            }
            ExtractJob(workflow, "test-windows-crossprocess").Should()
                .Contain("FullyQualifiedName~Issue2904_Tests.Actual_test_host_matches_requested_architecture");
        }

        [Fact]
        public void Evidence_matrix_also_selects_the_requested_architecture()
        {
            var workflow = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(), ".github", "workflows", "_bugfix-full-ci-matrix.yml"));
            foreach (var job in new[] { "test", "test-windows-crossprocess" })
            {
                var body = ExtractJob(workflow, job);
                body.Should().Contain("LITEDB_TEST_ARCH: ${{ matrix.platform.arch }}");
                body.Should().Contain("if: matrix.platform.arch == 'x86'");
                body.Should().Contain("install-x86-test-runtime.ps1");
                foreach (var marker in new[] { "- name: Build test project", "- name: Discover exact", "- name: Run " })
                    ExtractStep(body, marker).Should().Contain("--arch").And.Contain("AppendRuntimeIdentifierToOutputPath=false");
            }
        }

        [Fact]
        public void Runtime_installer_keeps_the_sdk_on_PATH()
        {
            var script = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(), ".github", "scripts", "install-x86-test-runtime.ps1"));
            script.Should().Contain("-Runtime dotnet -Channel $channel -Architecture x86")
                .And.Contain("-NoPath").And.Contain("DOTNET_ROOT_X86=")
                .And.NotContain("GITHUB_PATH");
        }

        [Fact]
        public void Actual_test_host_matches_requested_architecture()
        {
            var expected = Environment.GetEnvironmentVariable("LITEDB_TEST_ARCH");
            if (string.IsNullOrEmpty(expected)) return;
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant().Should().Be(expected);
        }

        private static string ExtractStep(string workflow, string marker)
        {
            var start = workflow.IndexOf(marker, StringComparison.Ordinal);
            start.Should().BeGreaterOrEqualTo(0);
            var end = workflow.IndexOf("\n      - name:", start + marker.Length, StringComparison.Ordinal);
            return end < 0 ? workflow.Substring(start) : workflow.Substring(start, end - start);
        }

        private static string ExtractJob(string workflow, string name)
        {
            var start = workflow.IndexOf("\n  " + name + ":\n", StringComparison.Ordinal);
            start.Should().BeGreaterOrEqualTo(0);
            var end = workflow.IndexOf("\n  ", start + 1, StringComparison.Ordinal);
            while (end >= 0 && workflow[end + 3] == ' ')
                end = workflow.IndexOf("\n  ", end + 1, StringComparison.Ordinal);
            return end < 0 ? workflow.Substring(start) : workflow.Substring(start, end - start);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LiteDB.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate the LiteDB repository root.");
        }
    }
}
