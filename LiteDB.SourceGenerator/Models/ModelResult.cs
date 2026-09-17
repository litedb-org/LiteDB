namespace LiteDB.SourceGenerator.Models;

internal sealed record ModelResult(
    ModelDescriptor? Model,
    string TypeName,
    DiagnosticLocationDescriptor? DiagnosticLocation,
    DiagnosticKind DiagnosticKind,
    string? Error)
{
    public bool IsSupported => Model is not null;

    public static ModelResult Supported(ModelDescriptor model) =>
        new(model, model.TypeName, null, DiagnosticKind.None, null);

    public static ModelResult InvalidModel(
        string typeName,
        DiagnosticLocationDescriptor diagnosticLocation,
        string error) =>
        new(null, typeName, diagnosticLocation, DiagnosticKind.InvalidModel, error);

    public static ModelResult InvalidProperty(
        string typeName,
        DiagnosticLocationDescriptor diagnosticLocation,
        string error) =>
        new(null, typeName, diagnosticLocation, DiagnosticKind.InvalidProperty, error);

    public static ModelResult MappingConflict(
        string typeName,
        DiagnosticLocationDescriptor diagnosticLocation,
        string error) =>
        new(null, typeName, diagnosticLocation, DiagnosticKind.MappingConflict, error);
}
