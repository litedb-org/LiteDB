using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Provides JSON serialization options shared between repro hosts and the CLI.
/// </summary>
public static class ReproJsonOptions
{
    /// <summary>
    /// Gets the default serializer options for repro messaging.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
