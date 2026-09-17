using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteDB.ReproRunner.Cli.Manifests;

/// <summary>
/// Validates repro manifest documents and produces strongly typed models.
/// </summary>
internal sealed partial class ManifestValidator
{
    private static ReproVariantOutcomeExpectations? ParseExpectedOutcomes(JsonElement root, ManifestValidationResult validation)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            validation.AddError("$.expectedOutcomes: expected object value.");
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "package",
            "latest"
        };

        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                validation.AddError($"$.expectedOutcomes.{property.Name}: unknown property.");
            }
        }

        ReproOutcomeExpectation? package = null;
        ReproOutcomeExpectation? latest = null;

        if (root.TryGetProperty("package", out var packageElement))
        {
            package = ParseOutcomeExpectation(packageElement, "$.expectedOutcomes.package", validation);
        }

        if (root.TryGetProperty("latest", out var latestElement))
        {
            latest = ParseOutcomeExpectation(latestElement, "$.expectedOutcomes.latest", validation);
            if (latest?.Kind is ReproOutcomeKind.HardFail or ReproOutcomeKind.Intermittent)
            {
                validation.AddError("$.expectedOutcomes.latest.kind: hardFail/intermittent are only supported for the package variant.");
            }
        }

        if (package is null && root.TryGetProperty("package", out _))
        {
            return null;
        }

        if (latest is null && root.TryGetProperty("latest", out _))
        {
            return null;
        }

        return new ReproVariantOutcomeExpectations(package, latest);
    }

    private static ReproOutcomeExpectation? ParseOutcomeExpectation(JsonElement element, string path, ManifestValidationResult validation)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            validation.AddError($"{path}: expected object value.");
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "kind",
            "exitCode",
            "logContains"
        };

        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                validation.AddError($"{path}.{property.Name}: unknown property.");
            }
        }

        if (!element.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            validation.AddError($"{path}.kind: expected string value.");
            return null;
        }

        var kindText = kindElement.GetString()?.Trim();
        if (string.IsNullOrEmpty(kindText))
        {
            validation.AddError($"{path}.kind: value must not be empty.");
            return null;
        }

        ReproOutcomeKind kind;
        switch (kindText.ToLowerInvariant())
        {
            case "reproduce":
                kind = ReproOutcomeKind.Reproduce;
                break;
            case "norepro":
                kind = ReproOutcomeKind.NoRepro;
                break;
            case "intermittent":
                kind = ReproOutcomeKind.Intermittent;
                break;
            case "hardfail":
                kind = ReproOutcomeKind.HardFail;
                break;
            default:
                validation.AddError($"{path}.kind: expected one of reproduce, norepro, hardFail, intermittent.");
                return null;
        }

        int? exitCode = null;
        if (element.TryGetProperty("exitCode", out var exitCodeElement))
        {
            if (exitCodeElement.ValueKind == JsonValueKind.Number && exitCodeElement.TryGetInt32(out var parsed))
            {
                exitCode = parsed;
            }
            else
            {
                validation.AddError($"{path}.exitCode: expected integer value.");
                return null;
            }
        }

        string? logContains = null;
        if (element.TryGetProperty("logContains", out var logElement))
        {
            if (logElement.ValueKind == JsonValueKind.String)
            {
                var value = logElement.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    validation.AddError($"{path}.logContains: value must not be empty when provided.");
                    return null;
                }

                logContains = value;
            }
            else
            {
                validation.AddError($"{path}.logContains: expected string value.");
                return null;
            }
        }

        return new ReproOutcomeExpectation(kind, exitCode, logContains);
    }
}
