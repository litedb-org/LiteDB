using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests;

internal static class GeneratorIncrementalityAssert
{
    public static readonly string[] ModelStepNames =
    [
        "BsonSourceGenerator.Models",
        "BsonSourceGenerator.CollectedModels"
    ];

    public static void AssertEquivalentOutputs(
        IReadOnlyDictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> firstSteps,
        IReadOnlyDictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> secondSteps,
        string name)
    {
        Assert.IsTrue(firstSteps.TryGetValue(name, out var firstNamedSteps));
        Assert.IsTrue(secondSteps.TryGetValue(name, out var secondNamedSteps));

        var firstValues = firstNamedSteps.SelectMany(step => step.Outputs).Select(output => output.Value).ToArray();
        var secondValues = secondNamedSteps.SelectMany(step => step.Outputs).Select(output => output.Value).ToArray();

        CollectionAssert.AreEqual(firstValues, secondValues, $"Tracked generator step '{name}' produced unequal values.");
    }

    public static void AssertCacheable(
        IReadOnlyDictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> steps,
        string name)
    {
        Assert.IsTrue(steps.TryGetValue(name, out var namedSteps), $"Tracked generator step '{name}' was not recorded.");

        var reasons = namedSteps
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToArray();

        Assert.IsTrue(reasons.Length > 0, $"Tracked generator step '{name}' produced no outputs.");
        Assert.IsTrue(
            reasons.All(reason => reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            $"Tracked generator step '{name}' was recomputed: {string.Join(", ", reasons)}.");
    }

    public static void AssertRecomputed(
        IReadOnlyDictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> steps,
        string name)
    {
        Assert.IsTrue(steps.TryGetValue(name, out var namedSteps), $"Tracked generator step '{name}' was not recorded.");
        Assert.IsTrue(
            namedSteps.SelectMany(step => step.Outputs).Any(output => output.Reason == IncrementalStepRunReason.Modified),
            $"Tracked generator step '{name}' did not observe the model change.");
    }

    public static void AssertContainsStableAndModifiedOutputs(
        IReadOnlyDictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> steps,
        string name)
    {
        Assert.IsTrue(steps.TryGetValue(name, out var namedSteps), $"Tracked generator step '{name}' was not recorded.");
        var actualReasons = namedSteps.SelectMany(step => step.Outputs).Select(output => output.Reason).ToArray();

        Assert.IsTrue(
            actualReasons.Any(reason => reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            $"Tracked generator step '{name}' did not reuse its unchanged model: {string.Join(", ", actualReasons)}.");
        CollectionAssert.Contains(actualReasons, IncrementalStepRunReason.Modified,
            $"Tracked generator step '{name}' did not recompute its changed model.");
    }

    public static void AssertNoRoslynObjects(
        IReadOnlyDictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> steps)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var stepName in ModelStepNames)
        {
            Assert.IsTrue(steps.TryGetValue(stepName, out var namedSteps));
            foreach (var value in namedSteps.SelectMany(step => step.Outputs).Select(output => output.Value))
            {
                AssertNoRoslynObjects(value, visited, stepName);
            }
        }
    }

    private static void AssertNoRoslynObjects(object? value, HashSet<object> visited, string stepName)
    {
        if (value is null)
        {
            return;
        }

        Assert.IsFalse(
            value is Compilation or ISymbol or SyntaxNode or SemanticModel or Location,
            $"Tracked generator step '{stepName}' retained Roslyn object '{value.GetType().FullName}'.");

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal or Type or MemberInfo)
        {
            return;
        }

        if (!type.IsValueType && !visited.Add(value))
        {
            return;
        }

        if (value is IEnumerable values)
        {
            foreach (var item in values)
            {
                AssertNoRoslynObjects(item, visited, stepName);
            }

            return;
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            AssertNoRoslynObjects(field.GetValue(value), visited, stepName);
        }
    }
}
