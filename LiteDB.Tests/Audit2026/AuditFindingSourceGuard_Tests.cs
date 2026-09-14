using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Audit2026
{
    /// <summary>
    /// Bounded guards for findings that cannot safely be reproduced in-process.
    /// Each row captures the three-line source context that implements the audited
    /// behavior. The guard remains red until that implementation is changed and a
    /// maintainer replaces the row with a behavioral/fault-injection regression.
    /// </summary>
    public class AuditFindingSourceGuard_Tests
    {
        public static IEnumerable<object[]> Findings()
        {
            var root = FindRepositoryRoot();
            var json = File.ReadAllText(Path.Combine(
                root,
                "LiteDB.Tests",
                "Audit2026",
                "audit-source-guards.json"));
            var findings = System.Text.Json.JsonSerializer.Deserialize<List<SourceGuardFinding>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (var finding in findings)
            {
                yield return new object[]
                {
                    finding.Id,
                    finding.File,
                    finding.Line,
                    finding.Title,
                    finding.Context
                };
            }
        }

        [Theory]
        [MemberData(nameof(Findings))]
        [Trait("Category", "AuditSourceGuard")]
        public void Audited_source_context_must_be_replaced(
            int id,
            string file,
            int line,
            string title,
            string context)
        {
            var root = FindRepositoryRoot();
            var sourcePath = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));

            File.Exists(sourcePath).Should().BeTrue(
                $"finding {id} must continue to resolve {file}:{line}");

            var source = File.ReadAllText(sourcePath).Replace("\r\n", "\n");
            var normalizedContext = context.Replace("\r\n", "\n");

            source.Contains(normalizedContext).Should().BeFalse(
                $"finding {id} is still present at {file}:{line}: {title}");
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LiteDB.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the LiteDB repository root.");
        }

        private sealed class SourceGuardFinding
        {
            public int Id { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
            public string Title { get; set; }
            public string Context { get; set; }
        }
    }
}
