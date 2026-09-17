using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace LiteDB.AotTestHost
{
    /// <summary>
    /// A minimal xunit v2 runner for the features LiteDB.Tests uses: Fact, Theory with InlineData/MemberData,
    /// Skip, an optional ITestOutputHelper constructor argument, IDisposable, and Task-returning tests.
    /// Tests run sequentially. Usage: LiteDB.Tests [results-file] [name-filter]
    /// </summary>
    internal static class Program
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

        private static int Main(string[] args)
        {
            var resultsPath = args.Length > 0 ? args[0] : "aot-test-results.tsv";
            var filter = args.Length > 1 ? args[1] : null;
            var results = new List<TestResult>();
            var clock = Stopwatch.StartNew();

            // Several tests open "../../../Resources/<file>" relative to the working directory, which for the
            // xunit runner is bin/<configuration>/<framework>. Recreate that depth below the copied Resources.
            resultsPath = Path.GetFullPath(resultsPath);
            var workingDirectory = Path.Combine(AppContext.BaseDirectory, "work", "configuration", "framework");
            Directory.CreateDirectory(workingDirectory);
            Directory.SetCurrentDirectory(workingDirectory);

            foreach (var test in Discover().Where(test => filter == null || test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                var result = Run(test);

                if (result.Outcome == Outcome.Timeout)
                {
                    // A hung test cannot be aborted, so the run ends here. Keep what was measured, record the
                    // test as failed, and exit non-zero so the validation script reports it by name.
                    results.Add(result with { Outcome = Outcome.Fail });
                    WriteResults(resultsPath, results);
                    Console.WriteLine($"TIMEOUT {result.Name}: {result.Detail}");
                    Environment.Exit(3);
                }

                results.Add(result);

                if (result.Outcome == Outcome.Fail)
                {
                    Console.WriteLine($"FAIL {result.Name}: {result.Detail}");
                }
            }

            WriteResults(resultsPath, results);

            Console.WriteLine();
            foreach (var group in results.GroupBy(result => result.Area).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{group.Key,-14} pass={group.Count(r => r.Outcome == Outcome.Pass),4} fail={group.Count(r => r.Outcome == Outcome.Fail),4} skip={group.Count(r => r.Outcome == Outcome.Skip),4}");
            }

            Console.WriteLine($"TOTAL pass={results.Count(r => r.Outcome == Outcome.Pass)} fail={results.Count(r => r.Outcome == Outcome.Fail)} skip={results.Count(r => r.Outcome == Outcome.Skip)} in {clock.Elapsed.TotalSeconds:F0}s");
            Console.WriteLine($"Dynamic code supported: {System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");

            // The exit code only reports that the run completed; outcomes are compared by the validation script.
            return 0;
        }

        private static void WriteResults(string path, IEnumerable<TestResult> results)
        {
            File.WriteAllLines(path, results
                .OrderBy(result => result.Name, StringComparer.Ordinal)
                .Select(result => $"{result.Outcome}\t{result.Name}\t{result.Detail}"));
        }

        private static IEnumerable<TestCase> Discover()
        {
            var types = typeof(Program).Assembly.GetTypes()
                .Where(type => type.IsClass && type.IsAbstract == false && type.IsGenericTypeDefinition == false)
                .OrderBy(type => type.FullName, StringComparer.Ordinal);

            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .OrderBy(method => method.Name, StringComparer.Ordinal);

                foreach (var method in methods)
                {
                    var fact = method.GetCustomAttribute<FactAttribute>();
                    if (fact == null) continue;

                    var name = $"{type.FullName}.{method.Name}";

                    if (fact is TheoryAttribute)
                    {
                        var rows = ReadTheoryRows(method, out var error);

                        if (error != null)
                        {
                            yield return new TestCase(type, method, null, name, fact.Skip, error);
                            continue;
                        }

                        for (var row = 0; row < rows.Length; row++)
                        {
                            yield return new TestCase(type, method, rows[row], $"{name}[{row}]", fact.Skip, null);
                        }
                    }
                    else
                    {
                        yield return new TestCase(type, method, Array.Empty<object>(), name, fact.Skip, null);
                    }
                }
            }
        }

        private static object[][] ReadTheoryRows(MethodInfo method, out string error)
        {
            try
            {
                error = null;
                return method.GetCustomAttributes<DataAttribute>().SelectMany(data => data.GetData(method)).ToArray();
            }
            catch (Exception exception)
            {
                error = Describe(exception);
                return null;
            }
        }

        private static TestResult Run(TestCase test)
        {
            if (test.Skip != null) return new TestResult(test.Name, Outcome.Skip, test.Skip);
            if (test.DiscoveryError != null) return new TestResult(test.Name, Outcome.Fail, "theory data: " + test.DiscoveryError);

            Exception failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    Execute(test);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            })
            { IsBackground = true };

            worker.Start();

            if (worker.Join(TestTimeout) == false)
            {
                return new TestResult(test.Name, Outcome.Timeout, $"exceeded {TestTimeout.TotalMinutes:F0} minutes");
            }

            return failure == null
                ? new TestResult(test.Name, Outcome.Pass, string.Empty)
                : new TestResult(test.Name, Outcome.Fail, Describe(failure));
        }

        private static void Execute(TestCase test)
        {
            object instance = null;

            try
            {
                if (test.Method.IsStatic == false)
                {
                    var constructor = test.Type.GetConstructors().Single();
                    var arguments = constructor.GetParameters()
                        .Select(parameter => parameter.ParameterType == typeof(ITestOutputHelper) ? (object)new NullOutput() : null)
                        .ToArray();
                    instance = constructor.Invoke(arguments);
                }

                var result = test.Method.Invoke(instance, ConvertArguments(test.Method, test.Arguments));

                if (result is Task task)
                {
                    task.GetAwaiter().GetResult();
                }
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// xunit converts InlineData literals to the parameter types and fills optional parameters.
        /// </summary>
        private static object[] ConvertArguments(MethodInfo method, object[] supplied)
        {
            // [InlineData(null)] binds null to the params array itself; xunit treats it as one null argument.
            supplied ??= new object[] { null };

            var parameters = method.GetParameters();
            var converted = new object[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];

                if (i >= supplied.Length)
                {
                    converted[i] = parameter.HasDefaultValue ? parameter.DefaultValue : null;
                    continue;
                }

                var value = supplied[i];
                var target = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

                if (value == null || target.IsInstanceOfType(value))
                {
                    converted[i] = value;
                }
                else if (target.IsEnum)
                {
                    converted[i] = Enum.ToObject(target, value);
                }
                else
                {
                    converted[i] = Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return converted;
        }

        private static string Describe(Exception exception)
        {
            var text = $"{exception.GetType().Name}: {exception.Message}";

            // Reflection and type-initializer wrappers hide the actual cause.
            for (var inner = exception.InnerException; inner != null; inner = inner.InnerException)
            {
                text += $" ---> {inner.GetType().Name}: {inner.Message}";
            }

            return text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }

        private enum Outcome
        {
            Pass,
            Fail,
            Skip,
            Timeout
        }

        private sealed record TestCase(Type Type, MethodInfo Method, object[] Arguments, string Name, string Skip, string DiscoveryError);

        private sealed record TestResult(string Name, Outcome Outcome, string Detail)
        {
            // LiteDB.Tests.<Area>.<Class>.<Method>
            public string Area => Name.Split('.').Skip(2).FirstOrDefault() ?? "(root)";
        }

        private sealed class NullOutput : ITestOutputHelper
        {
            public void WriteLine(string message)
            {
            }

            public void WriteLine(string format, params object[] args)
            {
            }
        }
    }
}
