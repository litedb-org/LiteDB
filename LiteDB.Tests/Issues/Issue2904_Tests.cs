using System;
using System.IO;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2904_Tests
    {
        [Fact]
        [Trait("Category", "PendingBug")]
        public void Windows_architecture_matrix_passes_its_architecture_to_both_test_hosts()
        {
            var workflow = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(), ".github", "workflows", "_reusable-ci.yml"));

            using var scope = new AssertionScope();
            ExtractStep(workflow, "- name: Run tests (${{ matrix.arch }})")
                .Should().Contain("--arch ${{ matrix.arch }}");
            ExtractStep(workflow, "- name: Run cross-process shared mode tests (${{ matrix.arch }})")
                .Should().Contain("--arch ${{ matrix.arch }}");
        }

        private static string ExtractStep(string workflow, string marker)
        {
            var start = workflow.IndexOf(marker, StringComparison.Ordinal);
            start.Should().BeGreaterOrEqualTo(0);
            var end = workflow.IndexOf("\n      - name:", start + marker.Length, StringComparison.Ordinal);
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
