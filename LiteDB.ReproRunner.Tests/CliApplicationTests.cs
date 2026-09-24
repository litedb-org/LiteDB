using LiteDB.ReproRunner.Cli.Commands;
using LiteDB.ReproRunner.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Testing;

namespace LiteDB.ReproRunner.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Validate_ReturnsErrorCodeWhenManifestInvalid()
    {
        var tempRoot = Directory.CreateTempSubdirectory();
        try
        {
            var reproRoot = Path.Combine(tempRoot.FullName, "LiteDB.ReproRunner");
            var reproDirectory = Path.Combine(reproRoot, "Repros", "BadRepro");
            Directory.CreateDirectory(reproDirectory);

            var manifest = """
            {
              "id": "Bad Repro",
              "title": "",
              "timeoutSeconds": 0,
              "requiresParallel": false,
              "defaultInstances": 0,
              "state": "unknown"
            }
            """;

            await File.WriteAllTextAsync(Path.Combine(reproDirectory, "repro.json"), manifest);
            await File.WriteAllTextAsync(Path.Combine(reproDirectory, "BadRepro.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            using var console = new TestConsole();
            var command = new ValidateCommand(console, new ReproRootLocator());
            var settings = new ValidateCommandSettings
            {
                All = true,
                Root = reproRoot
            };

            var exitCode = command.Execute(null!, settings);

            Assert.Equal(2, exitCode);
            Assert.Contains("INVALID", console.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot.FullName, true);
        }
    }
}
