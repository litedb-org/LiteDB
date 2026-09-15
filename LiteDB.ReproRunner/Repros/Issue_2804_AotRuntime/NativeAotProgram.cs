using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using LiteDB;

internal static class NativeAotProgram
{
    private delegate int Increment(int value);

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties,
        typeof(NativeCustomer))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties,
        typeof(NativeCoordinates))]
    private static int Main()
    {
        Console.WriteLine(
            $"NATIVEAOT_RUNTIME_2804: dynamicSupported={RuntimeFeature.IsDynamicCodeSupported}, " +
            $"dynamicCompiled={RuntimeFeature.IsDynamicCodeCompiled}");

        try
        {
            RunRuntimeControls();
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine("HARNESS_2804_ERROR: NativeAOT control failed.");
            Console.Error.WriteLine(failure);
            return 20;
        }

        try
        {
            var result = BsonExpression.Create("$.Value + 1")
                .ExecuteScalar(new BsonDocument { ["Value"] = 41 });
            if (!result.IsInt32 || result.AsInt32 != 42)
            {
                throw new InvalidOperationException($"Expression returned {result}, not 42.");
            }

            Console.WriteLine("NATIVEAOT_EXPRESSION_PASSED_2804");
        }
        catch (Exception failure)
        {
            WriteLiteDbFailure("expression", failure);
            return 1;
        }

        try
        {
            RunMapperProbe();
            Console.WriteLine("NATIVEAOT_MAPPING_PASSED_2804");
        }
        catch (Exception failure)
        {
            WriteLiteDbFailure("mapping", failure);
            return 1;
        }

        Console.WriteLine(
            "VERIFIED_2804_NATIVEAOT: dynamic code was disabled and the expression and mapping oracles passed");
        return 10;
    }

    private static void RunRuntimeControls()
    {
        if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
        {
            throw new InvalidOperationException("The control executable is not running as NativeAOT.");
        }

        var value = Expression.Parameter(typeof(int), "value");
        var lambda = Expression.Lambda<Increment>(
            Expression.Add(value, Expression.Constant(1)),
            value);
        var increment = lambda.Compile(preferInterpretation: true);
        if (increment(41) != 42)
        {
            throw new InvalidOperationException("The independent expression interpreter control failed.");
        }

        var instance = Activator.CreateInstance(typeof(NativeCustomer)) ??
            throw new InvalidOperationException("Activator control did not create the fixture.");
        var property = typeof(NativeCustomer).GetProperty(nameof(NativeCustomer.Name)) ??
            throw new InvalidOperationException("Reflection control did not find the fixture property.");
        property.SetValue(instance, "control");
        if (!Equals(property.GetValue(instance), "control"))
        {
            throw new InvalidOperationException("Reflection get/set control failed.");
        }

        Console.WriteLine("NATIVEAOT_PRECONDITIONS_PASSED_2804");
    }

    private static void RunMapperProbe()
    {
        var mapper = new BsonMapper();
        var expected = new NativeCustomer
        {
            Id = 7,
            Name = "Ada",
            Phones = new[] { "one", "two" },
            IsActive = true,
            Location = new NativeCoordinates { X = 3, Y = 4 }
        };

        var document = mapper.ToDocument(expected);
        if (document["_id"].AsInt32 != 7 || document["Name"].AsString != "Ada")
        {
            throw new InvalidOperationException("Mapper getter output did not match the fixture.");
        }

        var actual = mapper.ToObject<NativeCustomer>(document);
        if (actual.Id != expected.Id || actual.Name != expected.Name ||
            actual.IsActive != expected.IsActive || !actual.Phones.SequenceEqual(expected.Phones) ||
            actual.Location.X != expected.Location.X || actual.Location.Y != expected.Location.Y)
        {
            throw new InvalidOperationException("Mapper setter output did not round-trip the fixture.");
        }
    }

    private static void WriteLiteDbFailure(string phase, Exception failure)
    {
        Console.Error.WriteLine($"LITEDB_AOT_FAILURE_2804: phase={phase}");
        Console.Error.WriteLine(failure);
    }
}

public sealed class NativeCustomer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string[] Phones { get; set; } = Array.Empty<string>();

    public bool IsActive { get; set; }

    public NativeCoordinates Location { get; set; }
}

public struct NativeCoordinates
{
    public int X { get; set; }

    public int Y { get; set; }
}
