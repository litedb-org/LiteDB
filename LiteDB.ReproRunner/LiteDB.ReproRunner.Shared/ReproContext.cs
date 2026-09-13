using System.Globalization;

namespace LiteDB.ReproRunner.Shared;

/// <summary>
/// Provides access to the execution context passed to repro processes by the host.
/// </summary>
public sealed class ReproContext
{
    private const string SharedDatabaseVariable = "LITEDB_RR_SHARED_DB";
    private const string InstanceIndexVariable = "LITEDB_RR_INSTANCE_INDEX";
    private const string TotalInstancesVariable = "LITEDB_RR_TOTAL_INSTANCES";

    private ReproContext(string? sharedDatabaseRoot, int instanceIndex, int totalInstances)
    {
        SharedDatabaseRoot = string.IsNullOrWhiteSpace(sharedDatabaseRoot) ? null : sharedDatabaseRoot;
        InstanceIndex = instanceIndex;
        TotalInstances = Math.Max(totalInstances, 1);
    }

    /// <summary>
    /// Gets the optional shared database root configured by the host. When <c>null</c>, repros should
    /// fall back to <see cref="AppContext.BaseDirectory"/> or another suitable location.
    /// </summary>
    public string? SharedDatabaseRoot { get; }

    /// <summary>
    /// Gets the index assigned to the current repro instance. The first instance is <c>0</c>.
    /// </summary>
    public int InstanceIndex { get; }

    /// <summary>
    /// Gets the total number of repro instances that were launched for the current run.
    /// </summary>
    public int TotalInstances { get; }

    /// <summary>
    /// Resolves a <see cref="ReproContext"/> from the current process environment.
    /// </summary>
    public static ReproContext FromEnvironment()
    {
        return FromEnvironment(Environment.GetEnvironmentVariable);
    }

    /// <summary>
    /// Resolves a <see cref="ReproContext"/> from the provided environment accessor.
    /// </summary>
    /// <param name="resolver">A delegate that retrieves environment variables by name.</param>
    public static ReproContext FromEnvironment(Func<string, string?> resolver)
    {
        if (resolver is null)
        {
            throw new ArgumentNullException(nameof(resolver));
        }

        var sharedDatabaseRoot = resolver(SharedDatabaseVariable);
        var instanceIndex = ParseOrDefault(resolver(InstanceIndexVariable));
        var totalInstances = ParseOrDefault(resolver(TotalInstancesVariable), 1);

        return new ReproContext(sharedDatabaseRoot, instanceIndex, totalInstances);
    }

    /// <summary>
    /// Attempts to resolve a <see cref="ReproContext"/> from the current environment.
    /// </summary>
    public static bool TryFromEnvironment(out ReproContext? context)
    {
        try
        {
            context = FromEnvironment();
            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }

    /// <summary>
    /// Returns a dictionary representation of the environment variables required to recreate the context.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToEnvironmentVariables()
    {
        var variables = new Dictionary<string, string>
        {
            [InstanceIndexVariable] = InstanceIndex.ToString(CultureInfo.InvariantCulture),
            [TotalInstancesVariable] = TotalInstances.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(SharedDatabaseRoot))
        {
            variables[SharedDatabaseVariable] = SharedDatabaseRoot!;
        }

        return variables;
    }

    private static int ParseOrDefault(string? value, int defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }
}
