namespace LiteDB.SourceGenerator.Models;

internal readonly record struct ModelAnalysisResult(
    ModelDescriptor? Model,
    DiagnosticKind DiagnosticKind,
    string? Error)
{
    public bool IsSupported => Model is not null;

    public static ModelAnalysisResult Supported(ModelDescriptor model) =>
        new(model, DiagnosticKind.None, null);

    public static ModelAnalysisResult InvalidModel(string error) =>
        new(null, DiagnosticKind.InvalidModel, error);

    public static ModelAnalysisResult InvalidProperty(string error) =>
        new(null, DiagnosticKind.InvalidProperty, error);

    public static ModelAnalysisResult MappingConflict(string error) =>
        new(null, DiagnosticKind.MappingConflict, error);
}
