namespace LiteDB.ReproRunner.Cli.Manifests;

/// <summary>
/// Collects validation errors produced while parsing a repro manifest.
/// </summary>
internal sealed class ManifestValidationResult
{
    private readonly List<string> _errors = new();

    /// <summary>
    /// Gets the collection of validation errors discovered for the manifest.
    /// </summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>
    /// Gets a value indicating whether the manifest passed validation.
    /// </summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>
    /// Records a new validation error for the manifest.
    /// </summary>
    /// <param name="message">The message describing the validation failure.</param>
    public void AddError(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _errors.Add(message.Trim());
        }
    }
}
