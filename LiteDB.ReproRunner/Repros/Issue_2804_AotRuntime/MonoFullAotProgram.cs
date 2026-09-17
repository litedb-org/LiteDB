using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using LiteDB;

public static class MonoFullAotProgram
{
    private delegate int RuntimeCompiledIncrement(int value);

    public static int Main(string[] args)
    {
        var mode = args.Length == 1 ? args[0] : string.Empty;
#if CURRENT_SOURCE
        if (mode == "interpreter") AppContext.SetSwitch("LiteDB.UseInterpreter", true);
#endif
        if (mode == "jit-precondition")
        {
            return VerifyRuntimeCompilation(dynamicCodeExpected: true);
        }

        if (mode == "aot-precondition")
        {
            return VerifyRuntimeCompilation(dynamicCodeExpected: false);
        }

        if (mode != "jit" && mode != "full-aot" && mode != "interpreter")
        {
            Console.Error.WriteLine(
                "MONO_HARNESS_ERROR_2804: expected a precondition, 'jit', or 'full-aot' mode");
            return 20;
        }

        try
        {
            RunRuntimeControl();
            Console.WriteLine("MONO_RUNTIME_CONTROL_PASSED_2804: mode=" + mode);
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine("MONO_RUNTIME_CONTROL_FAILED_2804: mode=" + mode);
            Console.Error.WriteLine(failure);
            return 20;
        }

        try
        {
            RunLiteDbProbe();
#if CURRENT_SOURCE
            if (mode == "interpreter")
            {
                var runtime = typeof(LiteDatabase).Assembly.GetType("LiteDB.RuntimeExpression", true);
                var canCompile = runtime.GetField("CanCompile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (canCompile == null || (bool)canCompile.GetValue(null))
                    throw new Exception("startup switch did not select the interpreter");
                Console.WriteLine("MONO_STARTUP_SWITCH_PASSED_2804");
            }
#endif
            Console.WriteLine("MONO_LITEDB_PROBE_PASSED_2804: mode=" + mode);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine("MONO_LITEDB_PROBE_FAILED_2804: mode=" + mode);
            Console.Error.WriteLine(failure);
            return 1;
        }
    }

    private static int VerifyRuntimeCompilation(bool dynamicCodeExpected)
    {
        try
        {
            var value = Expression.Parameter(typeof(int), "value");
            var lambda = Expression.Lambda<RuntimeCompiledIncrement>(
                Expression.Add(value, Expression.Constant(1)),
                value);
            var increment = lambda.Compile();
            var result = increment(41);
            if (dynamicCodeExpected && result == 42)
            {
                Console.WriteLine("MONO_JIT_PRECONDITION_PASSED_2804");
                return 0;
            }

            Console.Error.WriteLine(
                "MONO_DYNAMIC_CODE_PRECONDITION_FAILED_2804: runtime-compiled result=" +
                result);
            return 20;
        }
        catch (ExecutionEngineException failure)
        {
            var message = failure.Message ?? string.Empty;
            if (!dynamicCodeExpected &&
                message.IndexOf("Attempting to JIT compile method", StringComparison.Ordinal) >= 0 &&
                message.IndexOf("aot-only mode", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Do not print the expected exception. The parent process must only classify a
                // dynamic-code signature emitted by the separate LiteDB target process.
                Console.WriteLine("MONO_NO_JIT_PRECONDITION_PASSED_2804");
                return 0;
            }

            Console.Error.WriteLine(
                "MONO_DYNAMIC_CODE_PRECONDITION_FAILED_2804: unexpected ExecutionEngineException");
            Console.Error.WriteLine(failure);
            return 20;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(
                "MONO_DYNAMIC_CODE_PRECONDITION_FAILED_2804: unexpected exception");
            Console.Error.WriteLine(failure);
            return 20;
        }
    }

    private static void RunRuntimeControl()
    {
        Func<int, int> increment = Increment;
        if (increment(41) != 42)
        {
            throw new Exception("statically compiled delegate returned the wrong value");
        }

        var instance = Activator.CreateInstance(typeof(RuntimeControl));
        var property = typeof(RuntimeControl).GetProperty("Value");
        if (instance == null || property == null)
        {
            throw new Exception("reflection control could not create or inspect its fixture");
        }

        property.SetValue(instance, "control", null);
        if (!object.Equals(property.GetValue(instance, null), "control"))
        {
            throw new Exception("reflection control did not round-trip its property value");
        }
    }

    private static int Increment(int value)
    {
        return value + 1;
    }

    private static void RunLiteDbProbe()
    {
        var expressionResult = BsonExpression.Create("$.Value + 1")
            .ExecuteScalar(new BsonDocument { ["Value"] = 41 });
        if (!expressionResult.IsInt32 || expressionResult.AsInt32 != 42)
        {
            throw new Exception("wrong expression result: " + expressionResult);
        }

        var mapper = new BsonMapper();
        var input = new Customer
        {
            Id = 7,
            Name = "Ada",
            Phones = new[] { "one", "two" },
            IsActive = true,
            Location = new Coordinates { X = 3, Y = 4 }
        };
        var output = mapper.ToObject<Customer>(mapper.ToDocument(input));
        if (output.Id != input.Id || output.Name != input.Name ||
            output.IsActive != input.IsActive || !output.Phones.SequenceEqual(input.Phones) ||
            output.Location.X != 3 || output.Location.Y != 4)
        {
            throw new Exception("typed mapping did not round-trip the fixture");
        }

        using (var stream = new MemoryStream())
        using (var database = new LiteDatabase(stream))
        {
            var customers = database.GetCollection<Customer>("customers");
            customers.Insert(new[]
            {
                new Customer { Id = 3, Name = "Grace", Phones = new[] { "three" }, IsActive = false },
                input,
                new Customer { Id = 11, Name = "Linus", Phones = new[] { "eleven" }, IsActive = true }
            });

            AssertQuery(customers, false);
            if (!customers.EnsureIndex(x => x.IsActive)) throw new Exception("secondary index was not created");
            AssertQuery(customers, true);

            var raw = database.GetCollection("customers").FindAll()
                .OrderBy(x => x["_id"].AsInt32).ToArray();
            if (raw.Length != 3 || raw[1]["Name"].AsString != "Ada" ||
                !raw[1]["Phones"].AsArray.Select(x => x.AsString).SequenceEqual(new[] { "one", "two" }))
            {
                throw new Exception("raw BSON ledger disagreed with the typed fixture");
            }
        }
    }

    private static void AssertQuery(ILiteCollection<Customer> customers, bool indexed)
    {
#if CURRENT_SOURCE
        // These captured forms were added after 5.0.21; preserve its original JIT control.
        var selected = new int?[] { 7 };
        if (!customers.Find(x => x.Id == selected[0].GetValueOrDefault()).Select(x => x.Id).SequenceEqual(new[] { 7 }))
            throw new Exception("nullable captured receiver returned wrong IDs");
        // Build explicitly: this mcs version incorrectly lowers a widened nullable coalesce.
        var coalesceTree = Expression.Lambda<Func<Customer, long>>(Expression.Coalesce(
            Expression.ArrayIndex(Expression.Constant(selected), Expression.Constant(0)), Expression.Constant(9L)),
            Expression.Parameter(typeof(Customer), "row"));
        var coalesced = new BsonMapper().GetExpression(coalesceTree).ExecuteScalar(new BsonDocument());
        if (!coalesced.IsInt64 || coalesced.AsInt64 != 7L)
            throw new Exception("nullable coalesce did not preserve Int64 result");
        Func<int> index = () => 0;
        var numbers = new[] { 7.9 };
        if (!customers.Find(x => x.Id == (int)numbers[index()]).Select(x => x.Id).SequenceEqual(new[] { 7 }))
            throw new Exception("captured delegate invocation or numeric conversion returned wrong IDs");

#endif
        var minimumId = 5;
        var actual = customers.Find(x => x.Id >= minimumId && x.IsActive)
            .Select(x => x.Id).OrderBy(x => x).ToArray();
        if (!actual.SequenceEqual(new[] { 7, 11 }))
        {
            throw new Exception("captured LINQ query returned wrong IDs (indexed=" + indexed + ")");
        }

        var rawIds = customers.Query().Where("_id >= @0 AND IsActive = true", minimumId)
            .ToDocuments().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
        if (!rawIds.SequenceEqual(new[] { 7, 11 }))
        {
            throw new Exception("BSON expression query disagreed with hard-coded IDs (indexed=" + indexed + ")");
        }
    }
}

public sealed class RuntimeControl
{
    public string Value { get; set; }
}

public sealed class Customer
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string[] Phones { get; set; }

    public bool IsActive { get; set; }

    public Coordinates Location { get; set; }
}

public struct Coordinates
{
    public int X { get; set; }

    public int Y { get; set; }
}
