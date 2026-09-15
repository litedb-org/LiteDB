using System;
using System.IO;
using System.Linq;
using System.Text.Json;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Audit2026
{
    public class AuditCoverageLedger_Tests
    {
        [Fact]
        [Trait("Category", "AuditInfrastructure")]
        public void Every_canonical_finding_has_a_source_guard()
        {
            var root = FindRepositoryRoot();
            var auditPath = Path.Combine(root, "docs", "audits", "2026-09", "findings-full.json");
            var guardPath = Path.Combine(root, "LiteDB.Tests", "Audit2026", "audit-source-guards.json");

            using var audit = JsonDocument.Parse(File.ReadAllText(auditPath));
            using var guards = JsonDocument.Parse(File.ReadAllText(guardPath));

            var canonicalIds = audit.RootElement
                .GetProperty("unique")
                .EnumerateArray()
                .Select(x => x.GetProperty("rank").GetInt32())
                .OrderBy(x => x)
                .ToArray();
            var guardIds = guards.RootElement
                .EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt32())
                .OrderBy(x => x)
                .ToArray();

            canonicalIds.Should().HaveCount(170);
            canonicalIds.Should().OnlyHaveUniqueItems();
            guardIds.Should().OnlyHaveUniqueItems();
            guardIds.Should().Equal(canonicalIds);
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
    }
}
