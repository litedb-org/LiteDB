using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2906_Tests
    {
        [Fact]
        [Trait("Category", "PendingBug")]
        public void Vector_index_topology_does_not_depend_on_an_unseeded_private_random()
        {
            var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "LiteDB", "Engine", "Services", "VectorIndexService.cs"));

            source.Should().NotContain(
                "private readonly Random _random = new Random();",
                "the pruning regression must be reproducible with deterministic level sampling");
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
