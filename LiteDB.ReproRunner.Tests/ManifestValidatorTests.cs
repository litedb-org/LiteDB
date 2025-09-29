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
        Assert.Equal(ReproState.Red, manifest.State);
        Assert.Null(manifest.ExpectedOutcomes.Package);
        Assert.Null(manifest.ExpectedOutcomes.Latest);
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

    [Fact]
    public void Validate_ParsesExpectedOutcomes()
    {
        const string json = """
        {
          "id": "Issue_002",
          "title": "Expected outcomes",
          "timeoutSeconds": 120,
          "requiresParallel": false,
          "defaultInstances": 1,
          "state": "green",
          "expectedOutcomes": {
            "package": {
              "kind": "hardFail",
              "exitCode": -5,
              "logContains": "NetworkException"
            },
            "latest": {
              "kind": "noRepro"
            }
          }
        }
        """;

        using var document = JsonDocument.Parse(json);
        var validation = new ManifestValidationResult();
        var validator = new ManifestValidator();

        var manifest = validator.Validate(document.RootElement, validation, out _);

        Assert.NotNull(manifest);
        Assert.True(validation.IsValid);
        Assert.Equal(ReproOutcomeKind.HardFail, manifest!.ExpectedOutcomes.Package!.Kind);
        Assert.Equal(-5, manifest.ExpectedOutcomes.Package!.ExitCode);
        Assert.Equal("NetworkException", manifest.ExpectedOutcomes.Package!.LogContains);
        Assert.Equal(ReproOutcomeKind.NoRepro, manifest.ExpectedOutcomes.Latest!.Kind);
    }

    [Fact]
    public void Validate_FailsWhenLatestHardFailDeclared()
    {
        const string json = """
        {
          "id": "Issue_003",
          "title": "Invalid latest expectation",
          "timeoutSeconds": 120,
          "requiresParallel": false,
          "defaultInstances": 1,
          "state": "red",
          "expectedOutcomes": {
            "latest": {
              "kind": "hardFail"
            }
          }
        }
        """;

        using var document = JsonDocument.Parse(json);
        var validation = new ManifestValidationResult();
        var validator = new ManifestValidator();

        var manifest = validator.Validate(document.RootElement, validation, out _);

        Assert.NotNull(manifest);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("expectedOutcomes.latest.kind", StringComparison.Ordinal));
    }
}
