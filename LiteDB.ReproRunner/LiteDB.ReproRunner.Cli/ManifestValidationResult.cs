namespace LiteDB.ReproRunner.Cli;

internal sealed class ManifestValidationResult
{
    private readonly List<string> _errors = new();

    public IReadOnlyList<string> Errors => _errors;

    public bool IsValid => _errors.Count == 0;

    public void AddError(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _errors.Add(message.Trim());
        }
    }
}
