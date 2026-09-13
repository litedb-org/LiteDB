using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteDB.ReproRunner.Cli.Manifests;

/// <summary>
/// Validates repro manifest documents and produces strongly typed models.
/// </summary>
internal sealed class ManifestValidator
{
    private static readonly Dictionary<string, ReproState> AllowedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = ReproState.Red,
        ["green"] = ReproState.Green,
        ["flaky"] = ReproState.Flaky
    };
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Validates the supplied JSON manifest and produces a <see cref="ReproManifest"/> instance.
    /// </summary>
    /// <param name="root">The root JSON element to evaluate.</param>
    /// <param name="validation">The validation result collector that will receive any errors.</param>
    /// <param name="rawId">When this method returns, contains the identifier parsed before validation succeeded.</param>
    /// <returns>The parsed manifest when validation succeeds; otherwise, <c>null</c>.</returns>
    public ReproManifest? Validate(JsonElement root, ManifestValidationResult validation, out string? rawId)
    {
        rawId = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            validation.AddError("Manifest root must be a JSON object.");
            return null;
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            map[property.Name] = property.Value;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "id",
            "title",
            "issues",
            "failingSince",
            "timeoutSeconds",
            "requiresParallel",
            "defaultInstances",
            "sharedDatabaseKey",
            "args",
            "tags",
            "state",
            "expectedOutcomes",
            "supports",
            "os"
        };

        foreach (var name in map.Keys)
        {
            if (!allowed.Contains(name))
            {
                validation.AddError($"$.{name}: unknown property.");
            }
        }

        string? id = null;
        if (map.TryGetValue("id", out var idElement))
        {
            if (idElement.ValueKind == JsonValueKind.String)
            {
                var value = idElement.GetString();
                rawId = value;

                if (string.IsNullOrWhiteSpace(value))
                {
                    validation.AddError("$.id: value must not be empty.");
                }
                else if (!IdPattern.IsMatch(value))
                {
                    validation.AddError($"$.id: must match ^[A-Za-z0-9_]+$ (got: {value}).");
                }
                else
                {
                    id = value;
                }
            }
            else
            {
                validation.AddError($"$.id: expected string (got: {DescribeKind(idElement.ValueKind)}).");
            }
        }
        else
        {
            validation.AddError("$.id: property is required.");
        }

        string? title = null;
        if (map.TryGetValue("title", out var titleElement))
        {
            if (titleElement.ValueKind == JsonValueKind.String)
            {
                var value = titleElement.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    validation.AddError("$.title: value must not be empty.");
                }
                else
                {
                    title = value!;
                }
            }
            else
            {
                validation.AddError($"$.title: expected string (got: {DescribeKind(titleElement.ValueKind)}).");
            }
        }
        else
        {
            validation.AddError("$.title: property is required.");
        }

        var issues = new List<string>();
        if (map.TryGetValue("issues", out var issuesElement))
        {
            if (issuesElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in issuesElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        validation.AddError($"$.issues[{index}]: expected string (got: {DescribeKind(item.ValueKind)}).");
                    }
                    else
                    {
                        var url = item.GetString();
                        var trimmed = url?.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || !Uri.TryCreate(trimmed, UriKind.Absolute, out _))
                        {
                            validation.AddError($"$.issues[{index}]: '{url}' is not a valid absolute URL.");
                        }
                        else
                        {
                            issues.Add(trimmed);
                        }
                    }

                    index++;
                }
            }
            else
            {
                validation.AddError("$.issues: expected an array of strings.");
            }
        }

        string? failingSince = null;
        if (map.TryGetValue("failingSince", out var failingElement))
        {
            if (failingElement.ValueKind == JsonValueKind.String)
            {
                var value = failingElement.GetString();
                var trimmed = value?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    failingSince = trimmed;
                }
                else
                {
                    validation.AddError("$.failingSince: value must not be empty when provided.");
                }
            }
            else
            {
                validation.AddError("$.failingSince: expected string value.");
            }
        }

        int? timeoutSeconds = null;
        if (map.TryGetValue("timeoutSeconds", out var timeoutElement))
        {
            if (timeoutElement.ValueKind == JsonValueKind.Number && timeoutElement.TryGetInt32(out var timeout))
            {
                if (timeout < 1 || timeout > 36000)
                {
                    validation.AddError("$.timeoutSeconds: expected integer between 1 and 36000.");
                }
                else
                {
                    timeoutSeconds = timeout;
                }
            }
            else
            {
                validation.AddError("$.timeoutSeconds: expected integer value.");
            }
        }
        else
        {
            validation.AddError("$.timeoutSeconds: property is required.");
        }

        bool? requiresParallel = null;
        if (map.TryGetValue("requiresParallel", out var parallelElement))
        {
            if (parallelElement.ValueKind == JsonValueKind.True || parallelElement.ValueKind == JsonValueKind.False)
            {
                requiresParallel = parallelElement.GetBoolean();
            }
            else
            {
                validation.AddError("$.requiresParallel: expected boolean value.");
            }
        }
        else
        {
            validation.AddError("$.requiresParallel: property is required.");
        }

        int? defaultInstances = null;
        if (map.TryGetValue("defaultInstances", out var instancesElement))
        {
            if (instancesElement.ValueKind == JsonValueKind.Number && instancesElement.TryGetInt32(out var instances))
            {
                if (instances < 1)
                {
                    validation.AddError("$.defaultInstances: expected integer >= 1.");
                }
                else
                {
                    defaultInstances = instances;
                }
            }
            else
            {
                validation.AddError("$.defaultInstances: expected integer value.");
            }
        }
        else
        {
            validation.AddError("$.defaultInstances: property is required.");
        }

        string? sharedDatabaseKey = null;
        if (map.TryGetValue("sharedDatabaseKey", out var sharedElement))
        {
            if (sharedElement.ValueKind == JsonValueKind.String)
            {
                var value = sharedElement.GetString();
                var trimmed = value?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    validation.AddError("$.sharedDatabaseKey: value must not be empty when provided.");
                }
                else
                {
                    sharedDatabaseKey = trimmed;
                }
            }
            else
            {
                validation.AddError("$.sharedDatabaseKey: expected string value.");
            }
        }

        var args = new List<string>();
        if (map.TryGetValue("args", out var argsElement))
        {
            if (argsElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in argsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        validation.AddError($"$.args[{index}]: expected string value.");
                    }
                    else
                    {
                        args.Add(item.GetString()!);
                    }

                    index++;
                }
            }
            else
            {
                validation.AddError("$.args: expected an array of strings.");
            }
        }

        var tags = new List<string>();
        if (map.TryGetValue("tags", out var tagsElement))
        {
            if (tagsElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in tagsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        validation.AddError($"$.tags[{index}]: expected string value.");
                    }
                    else
                    {
                        var tag = item.GetString();
                        var trimmed = tag?.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            tags.Add(trimmed!);
                        }
                        else
                        {
                            validation.AddError($"$.tags[{index}]: value must not be empty.");
                        }
                    }

                    index++;
                }
            }
            else
            {
                validation.AddError("$.tags: expected an array of strings.");
            }
        }

        var supports = new List<string>();
        if (map.TryGetValue("supports", out var supportsElement))
        {
            if (supportsElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in supportsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        validation.AddError($"$.supports[{index}]: expected string value.");
                    }
                    else
                    {
                        var raw = item.GetString();
                        var trimmed = raw?.Trim();
                        if (string.IsNullOrEmpty(trimmed))
                        {
                            validation.AddError($"$.supports[{index}]: value must not be empty.");
                        }
                        else
                        {
                            var normalized = trimmed.ToLowerInvariant();
                            if (normalized != "windows" && normalized != "linux" && normalized != "any")
                            {
                                validation.AddError($"$.supports[{index}]: expected one of windows, linux, any.");
                            }
                            else if (normalized == "any" && seen.Count > 0)
                            {
                                validation.AddError("$.supports: 'any' cannot be combined with other platform values.");
                            }
                            else if (normalized != "any" && seen.Contains("any"))
                            {
                                validation.AddError("$.supports: 'any' cannot be combined with other platform values.");
                            }
                            else if (seen.Add(normalized))
                            {
                                supports.Add(normalized);
                            }
                        }
                    }

                    index++;
                }
            }
            else if (supportsElement.ValueKind != JsonValueKind.Null)
            {
                validation.AddError("$.supports: expected an array of strings.");
            }
        }

        ReproOsConstraints? osConstraints = null;
        if (map.TryGetValue("os", out var osElement))
        {
            if (osElement.ValueKind == JsonValueKind.Object)
            {
                osConstraints = ParseOsConstraints(osElement, validation);
            }
            else if (osElement.ValueKind != JsonValueKind.Null)
            {
                validation.AddError("$.os: expected object value.");
            }
        }

        ReproState? state = null;
        if (map.TryGetValue("state", out var stateElement))
        {
            if (stateElement.ValueKind == JsonValueKind.String)
            {
                var value = stateElement.GetString()?.Trim();
                if (string.IsNullOrEmpty(value) || !AllowedStates.TryGetValue(value, out var parsedState))
                {
                    validation.AddError("$.state: expected one of red, green, flaky.");
                }
                else
                {
                    state = parsedState;
                }
            }
            else
            {
                validation.AddError("$.state: expected string value.");
            }
        }
        else
        {
            validation.AddError("$.state: property is required.");
        }

        ReproVariantOutcomeExpectations? expectedOutcomes = ReproVariantOutcomeExpectations.Empty;
        if (map.TryGetValue("expectedOutcomes", out var expectedOutcomesElement))
        {
            expectedOutcomes = ParseExpectedOutcomes(expectedOutcomesElement, validation);
        }

        if (requiresParallel == true)
        {
            if (defaultInstances.HasValue && defaultInstances.Value < 2)
            {
                validation.AddError("$.defaultInstances: must be >= 2 when requiresParallel is true.");
            }

            if (string.IsNullOrWhiteSpace(sharedDatabaseKey))
            {
                validation.AddError("$.sharedDatabaseKey: required when requiresParallel is true.");
            }
        }

        if (id is null || title is null || timeoutSeconds is null || requiresParallel is null || defaultInstances is null || state is null || expectedOutcomes is null)
        {
            return null;
        }

        var issuesArray = issues.Count > 0 ? issues.ToArray() : Array.Empty<string>();
        var argsArray = args.Count > 0 ? args.ToArray() : Array.Empty<string>();
        var tagsArray = tags.Count > 0 ? tags.ToArray() : Array.Empty<string>();
        var supportsArray = supports.Count > 0 ? supports.ToArray() : Array.Empty<string>();

        return new ReproManifest(
            id,
            title,
            issuesArray,
            failingSince,
            timeoutSeconds.Value,
            requiresParallel.Value,
            defaultInstances.Value,
            sharedDatabaseKey,
            argsArray,
            tagsArray,
            state.Value,
            expectedOutcomes,
            supportsArray,
            osConstraints);
    }

    private static ReproOsConstraints? ParseOsConstraints(JsonElement root, ManifestValidationResult validation)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "includePlatforms",
            "includeLabels",
            "excludePlatforms",
            "excludeLabels"
        };

        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                validation.AddError($"$.os.{property.Name}: unknown property.");
            }
        }

        var includePlatforms = ParsePlatformArray(root, "includePlatforms", validation);
        var includeLabels = ParseLabelArray(root, "includeLabels", validation);
        var excludePlatforms = ParsePlatformArray(root, "excludePlatforms", validation);
        var excludeLabels = ParseLabelArray(root, "excludeLabels", validation);

        if (includePlatforms is null || includeLabels is null || excludePlatforms is null || excludeLabels is null)
        {
            return null;
        }

        return new ReproOsConstraints(includePlatforms, includeLabels, excludePlatforms, excludeLabels);
    }

    private static IReadOnlyList<string>? ParsePlatformArray(JsonElement root, string propertyName, ManifestValidationResult validation)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            validation.AddError($"$.os.{propertyName}: expected an array of strings.");
            return null;
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                validation.AddError($"$.os.{propertyName}[{index}]: expected string value.");
            }
            else
            {
                var value = item.GetString();
                var trimmed = value?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    validation.AddError($"$.os.{propertyName}[{index}]: value must not be empty.");
                }
                else
                {
                    var normalized = trimmed.ToLowerInvariant();
                    if (normalized != "windows" && normalized != "linux")
                    {
                        validation.AddError($"$.os.{propertyName}[{index}]: expected one of windows, linux.");
                    }
                    else if (seen.Add(normalized))
                    {
                        values.Add(normalized);
                    }
                }
            }

            index++;
        }

        return values;
    }

    private static IReadOnlyList<string>? ParseLabelArray(JsonElement root, string propertyName, ManifestValidationResult validation)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            validation.AddError($"$.os.{propertyName}: expected an array of strings.");
            return null;
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                validation.AddError($"$.os.{propertyName}[{index}]: expected string value.");
            }
            else
            {
                var value = item.GetString();
                var trimmed = value?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    validation.AddError($"$.os.{propertyName}[{index}]: value must not be empty.");
                }
                else if (seen.Add(trimmed))
                {
                    values.Add(trimmed);
                }
            }

            index++;
        }

        return values;
    }

    private static string DescribeKind(JsonValueKind kind)
    {
        return kind switch
        {
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True => "boolean",
            JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => kind.ToString().ToLowerInvariant()
        };
    }

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
            if (latest?.Kind == ReproOutcomeKind.HardFail)
            {
                validation.AddError("$.expectedOutcomes.latest.kind: hardFail is only supported for the package variant.");
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
            case "hardfail":
                kind = ReproOutcomeKind.HardFail;
                break;
            default:
                validation.AddError($"{path}.kind: expected one of reproduce, norepro, hardFail.");
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
