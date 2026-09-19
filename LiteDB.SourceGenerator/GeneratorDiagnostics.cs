using System;
using System.Collections.Immutable;

using LiteDB.SourceGenerator.Models;

using Microsoft.CodeAnalysis;

namespace LiteDB.SourceGenerator;

internal static class GeneratorDiagnostics
{
    private static readonly DiagnosticDescriptor InvalidModel = new(
        id: "LDBSG001",
        title: "Invalid source-generated model",
        messageFormat: "Type '{0}' cannot use BsonSourceGenerated: {1}",
        category: "LiteDB.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidProperty = new(
        id: "LDBSG002",
        title: "Invalid source-generated property",
        messageFormat: "Type '{0}' cannot use BsonSourceGenerated: {1}",
        category: "LiteDB.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MappingConflict = new(
        id: "LDBSG003",
        title: "Conflicting source-generated mapping",
        messageFormat: "Type '{0}' cannot use BsonSourceGenerated: {1}",
        category: "LiteDB.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(InvalidModel, InvalidProperty, MappingConflict);

    public static DiagnosticDescriptor GetDescriptor(DiagnosticKind kind) => kind switch
    {
        DiagnosticKind.InvalidModel => InvalidModel,
        DiagnosticKind.InvalidProperty => InvalidProperty,
        DiagnosticKind.MappingConflict => MappingConflict,
        _ => throw new InvalidOperationException($"Unsupported diagnostic kind '{kind}'.")
    };
}
