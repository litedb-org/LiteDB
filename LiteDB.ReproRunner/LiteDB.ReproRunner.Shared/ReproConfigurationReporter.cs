using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.ReproRunner.Shared.Messaging;

namespace LiteDB.ReproRunner.Shared;

/// <summary>
/// Provides helpers for reporting the repro build configuration back to the host.
/// </summary>
public static class ReproConfigurationReporter
{
    private const string UseProjectReferenceMetadataKey = "LiteDB.ReproRunner.UseProjectReference";
    private const string LiteDbPackageVersionMetadataKey = "LiteDB.ReproRunner.LiteDBPackageVersion";

    /// <summary>
    /// Reads the repro configuration metadata from the entry assembly and sends it to the host synchronously.
    /// </summary>
    /// <param name="client">The client used to communicate with the host.</param>
    public static void SendConfiguration(ReproHostClient client)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        var (useProjectReference, packageVersion) = ReadConfiguration();
        client.SendConfiguration(useProjectReference, packageVersion);
    }

    /// <summary>
    /// Reads the repro configuration metadata from the entry assembly and sends it to the host asynchronously.
    /// </summary>
    /// <param name="client">The client used to communicate with the host.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>A task that completes when the configuration has been transmitted.</returns>
    public static Task SendConfigurationAsync(ReproHostClient client, CancellationToken cancellationToken = default)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        var (useProjectReference, packageVersion) = ReadConfiguration();
        return client.SendConfigurationAsync(useProjectReference, packageVersion, cancellationToken);
    }

    private static (bool UseProjectReference, string? LiteDbPackageVersion) ReadConfiguration()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();

        string? useProjectReferenceValue = null;
        string? packageVersionValue = null;

        foreach (var attribute in metadata)
        {
            if (string.Equals(attribute.Key, UseProjectReferenceMetadataKey, StringComparison.Ordinal))
            {
                useProjectReferenceValue = attribute.Value;
            }
            else if (string.Equals(attribute.Key, LiteDbPackageVersionMetadataKey, StringComparison.Ordinal))
            {
                packageVersionValue = attribute.Value;
            }
        }

        var useProjectReference = ParseBoolean(useProjectReferenceValue);
        var packageVersion = string.IsNullOrWhiteSpace(packageVersionValue) ? null : packageVersionValue;
        return (useProjectReference, packageVersion);
    }

    private static bool ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}
