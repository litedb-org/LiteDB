using System.Text.Json;
using LiteDB.ReproRunner.Cli.Manifests;

namespace LiteDB.ReproRunner.Tests;

public sealed class ManifestValidatorTests
{
    [Fact]
    public void Validate_AllowsValidManifest()
    {
        const string json = """
        {
          "id": "Issue_000_Demo",
          "title": "Demo repro",
          "issues": ["https://example.com/issue"],
          "failingSince": "5.0.x",
          "timeoutSeconds": 120,
          "requiresParallel": false,
          "defaultInstances": 1,
          "sharedDatabaseKey": "demo",
          "args": ["--flag"],
          "tags": ["demo"],
          "state": "red"
        }
        """;

        using var document = JsonDocument.Parse(json);
        var validation = new ManifestValidationResult();
        var validator = new ManifestValidator();

        var manifest = validator.Validate(document.RootElement, validation, out var rawId);

        Assert.NotNull(manifest);
        Assert.True(validation.IsValid);
        Assert.Equal("Issue_000_Demo", manifest!.Id);
        Assert.Equal("Issue_000_Demo", rawId);
        Assert.Equal(120, manifest.TimeoutSeconds);
        Assert.Equal("red", manifest.State);
    }

    [Fact]
    public void Validate_FailsWhenTimeoutOutOfRange()
    {
        const string json = """
        {
          "id": "Issue_001",
          "title": "Invalid timeout",
          "timeoutSeconds": 0,
          "requiresParallel": false,
          "defaultInstances": 1,
          "state": "green"
        }
        """;

        using var document = JsonDocument.Parse(json);
        var validation = new ManifestValidationResult();
        var validator = new ManifestValidator();

        var manifest = validator.Validate(document.RootElement, validation, out _);

        Assert.Null(manifest);
        Assert.Contains(validation.Errors, error => error.Contains("$.timeoutSeconds", StringComparison.Ordinal));
    }
}
