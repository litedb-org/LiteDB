using LiteDB.ReproRunner.Cli;

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
            var app = new CliApplication(console);
            var exitCode = await app.RunAsync(new[] { "--root", reproRoot, "validate" });

            Assert.Equal(2, exitCode);
            Assert.Contains("INVALID", console.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot.FullName, true);
        }
    }
}
