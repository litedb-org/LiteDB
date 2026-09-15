using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private const string MonoImage =
        "mono:6.12.0.182@sha256:34d816779b1248b5cfd095770b64ecbaf1798e2aca693a91c11a018dce9c7ad5";

    private static int Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var scratch = Path.Combine(Path.GetTempPath(), "litedb-2804-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            var nativeResult = RunNativeAot(scratch);
            Console.Write(nativeResult.Output);
            Console.WriteLine($"NATIVEAOT_RUNTIME_EXIT_2804: exit={nativeResult.ExitCode}");
            if (nativeResult.ExitCode == 1 && NativeAotPreconditionsPassed(nativeResult) &&
                HasMarker(nativeResult, "LITEDB_AOT_FAILURE_2804") &&
                IsExactLiteDbAotFailure(nativeResult.Output))
            {
                Console.WriteLine(
                    "BUG_2804_CONFIRMED: Linux NativeAOT controls passed, then LiteDB failed with the exact dynamic-code signature");
                return 0;
            }
            if (!NativeAotProbePassed(nativeResult))
            {
                return HarnessFailure($"NativeAOT probe returned an unclassified result (exit {nativeResult.ExitCode}).");
            }
            var monoResult = RunMonoFullAot(scratch);
            Console.Write(monoResult.Output);
            Console.WriteLine($"MONO_PROCESS_EXIT_2804: exit={monoResult.ExitCode}");
            var sourceCompilePassed = HasMarker(monoResult, "MONO_SOURCE_COMPILE_PASSED_2804");
            var jitPreconditionPassed = HasMarker(monoResult, "MONO_JIT_PRECONDITION_PASSED_2804");
            var jitControlPassed = HasMarker(monoResult, "MONO_RUNTIME_CONTROL_PASSED_2804: mode=jit");
            var jitLiteDbPassed = HasMarker(monoResult, "MONO_LITEDB_PROBE_PASSED_2804: mode=jit");
            var fullAotCompilePassed = HasMarker(monoResult, "MONO_FULL_AOT_COMPILE_PASSED_2804");
            var noJitPreconditionPassed = HasMarker(monoResult,
                "MONO_NO_JIT_PRECONDITION_PASSED_2804");
            var fullAotControlPassed = HasMarker(monoResult,
                "MONO_RUNTIME_CONTROL_PASSED_2804: mode=full-aot");
            var fullAotLiteDbFailed = HasMarker(monoResult,
                "MONO_LITEDB_PROBE_FAILED_2804: mode=full-aot");
            if (monoResult.ExitCode == 1 && sourceCompilePassed && jitPreconditionPassed &&
                jitControlPassed && jitLiteDbPassed &&
                fullAotCompilePassed && noJitPreconditionPassed && fullAotControlPassed &&
                fullAotLiteDbFailed && IsExactLiteDbAotFailure(monoResult.Output))
            {
                Console.WriteLine(
                    "BUG_2804_CONFIRMED: NativeAOT, JIT, and no-JIT controls passed, then Mono --full-aot attempted JIT compilation inside LiteDB");
                return 0;
            }
            var fullAotLiteDbPassed = HasMarker(monoResult,
                "MONO_LITEDB_PROBE_PASSED_2804: mode=full-aot");
            if (monoResult.ExitCode == 0 && sourceCompilePassed &&
                jitPreconditionPassed && jitControlPassed && jitLiteDbPassed && fullAotCompilePassed &&
                noJitPreconditionPassed && fullAotControlPassed && fullAotLiteDbPassed)
            {
                Console.WriteLine(
                    "VERIFIED_2804: Linux NativeAOT and Mono --full-aot expression, mapping, query, and index oracles passed");
                return 10;
            }
            return HarnessFailure($"Mono full-AOT control returned an unclassified result (exit {monoResult.ExitCode}).");
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine("HARNESS_2804_ERROR");
            Console.Error.WriteLine(failure);
            return 20;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static ProcessResult RunNativeAot(string scratch)
    {
        if (!OperatingSystem.IsLinux() ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new PlatformNotSupportedException("The NativeAOT control supports Linux x64 and arm64 hosts.");
        }
        var projectPath = GetMetadata("LiteDB.ReproRunner.ProjectPath");
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("The NativeAOT control project is missing.", projectPath);
        }

        var useProjectReference = GetMetadata("LiteDB.ReproRunner.UseProjectReference");
        var packageVersion = GetMetadata("LiteDB.ReproRunner.LiteDBPackageVersion");
        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? "linux-x64"
            : "linux-arm64";
        var publishDirectory = Path.Combine(scratch, "native-publish");
        var artifactsDirectory = Path.Combine(scratch, "native-artifacts");
        var arguments = new[]
        {
            "publish", projectPath,
            "-c", "Release",
            "-f", "net8.0",
            "-r", runtimeIdentifier,
            "--self-contained", "true",
            "--nologo",
            "--artifacts-path", artifactsDirectory,
            "-o", publishDirectory,
            "-p:NativeProbeBuild=true",
            $"-p:UseProjectReference={useProjectReference}",
            $"-p:LiteDBPackageVersion={packageVersion}",
            "-p:TestingEnabled=false",
            "-p:GitVersionEnabled=false"
        };
        var publish = RunProcess("dotnet", arguments, Path.GetDirectoryName(projectPath)!, 180_000);
        if (publish.ExitCode != 0)
        {
            Console.Error.WriteLine($"NATIVEAOT_PUBLISH_FAILED_2804: exit={publish.ExitCode}");
            Console.Error.WriteLine(publish.Output);
            throw new InvalidOperationException($"NativeAOT publish failed with exit {publish.ExitCode}.");
        }

        Console.WriteLine("NATIVEAOT_PUBLISH_PASSED_2804");
        var executable = Path.Combine(publishDirectory, "Issue_2804_AotRuntime");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("NativeAOT publish did not produce the expected executable.", executable);
        }

        Console.WriteLine("NATIVEAOT_RUNTIME_STARTED_2804");
        return RunProcess(executable, Array.Empty<string>(), publishDirectory, 30_000);
    }

    private static ProcessResult RunMonoFullAot(string scratch)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "MonoFullAotProgram.cs");
        var staged = Path.Combine(AppContext.BaseDirectory, "mono-input");
        if (!File.Exists(source) || !File.Exists(Path.Combine(staged, "LiteDB.dll")))
        {
            throw new InvalidOperationException("Mono source or staged LiteDB dependency is missing.");
        }

        var monoDirectory = Path.Combine(scratch, "mono");
        Directory.CreateDirectory(monoDirectory);
        File.Copy(source, Path.Combine(monoDirectory, "MonoFullAotProgram.cs"));
        foreach (var dependency in Directory.EnumerateFiles(staged, "*.dll"))
        {
            File.Copy(dependency, Path.Combine(monoDirectory, Path.GetFileName(dependency)));
        }

        const string script = """
set -e
echo MONO_CONTAINER_STARTED_2804
if ! mcs -langversion:7.2 -out:Issue2804.exe -r:LiteDB.dll -r:/usr/lib/mono/4.7.2-api/Facades/netstandard.dll MonoFullAotProgram.cs >source-compile.log 2>&1
then
  echo MONO_SOURCE_COMPILE_FAILED_2804
  cat source-compile.log
  exit 81
fi
echo MONO_SOURCE_COMPILE_PASSED_2804
mono Issue2804.exe jit-precondition
mono Issue2804.exe jit
for assembly in \
  /usr/lib/mono/4.5/mscorlib.dll /usr/lib/mono/4.5/System.dll \
  /usr/lib/mono/4.5/System.Core.dll /usr/lib/mono/4.5/Facades/netstandard.dll \
  /usr/lib/mono/gac/Mono.Security/4.0.0.0__0738eb9f132ed756/Mono.Security.dll \
  /usr/lib/mono/gac/System.Xml/4.0.0.0__b77a5c561934e089/System.Xml.dll /usr/lib/mono/gac/System.Configuration/4.0.0.0__b03f5f7f11d50a3a/System.Configuration.dll \
  /usr/lib/mono/gac/System.Security/4.0.0.0__b03f5f7f11d50a3a/System.Security.dll /usr/lib/mono/gac/System.Numerics/4.0.0.0__b77a5c561934e089/System.Numerics.dll \
  System.Buffers.dll System.Memory.dll System.Runtime.CompilerServices.Unsafe.dll LiteDB.dll Issue2804.exe
do
  if ! mono --aot=full "$assembly" >aot-compile.log 2>&1
  then
    echo "MONO_FULL_AOT_COMPILE_FAILED_2804: assembly=$assembly"
    cat aot-compile.log
    exit 82
  fi
done
echo MONO_FULL_AOT_COMPILE_PASSED_2804
mono --full-aot Issue2804.exe aot-precondition
mono --full-aot Issue2804.exe full-aot
""";

        return RunProcess(
            "docker",
            new[]
            {
                "run", "--rm", "--pull=missing",
                "-v", monoDirectory + ":/work",
                "-w", "/work",
                MonoImage,
                "bash", "-lc", script
            },
            monoDirectory,
            300_000);
    }

    private static bool NativeAotPreconditionsPassed(ProcessResult result)
    {
        return result.Output.Contains(
                "dynamicSupported=False, dynamicCompiled=False",
                StringComparison.Ordinal) &&
            result.Output.Contains("NATIVEAOT_PRECONDITIONS_PASSED_2804", StringComparison.Ordinal);
    }

    private static bool NativeAotProbePassed(ProcessResult result)
    {
        return result.ExitCode == 10 && NativeAotPreconditionsPassed(result) &&
            result.Output.Contains("NATIVEAOT_EXPRESSION_PASSED_2804", StringComparison.Ordinal) &&
            result.Output.Contains("NATIVEAOT_MAPPING_PASSED_2804", StringComparison.Ordinal) &&
            result.Output.Contains("VERIFIED_2804_NATIVEAOT", StringComparison.Ordinal);
    }

    private static bool IsExactLiteDbAotFailure(string output)
    {
        var liteDbFrame = output.Contains("LiteDB.BsonExpression", StringComparison.Ordinal) ||
            output.Contains("LiteDB.Reflection", StringComparison.Ordinal) ||
            output.Contains("Reflection.CreateGeneric", StringComparison.Ordinal) ||
            output.Contains("LiteDB.LinqExpressionVisitor", StringComparison.Ordinal);
        var executionEngineFailure = output.Contains("System.ExecutionEngineException", StringComparison.Ordinal) &&
            output.Contains("Attempting to JIT compile method", StringComparison.Ordinal) &&
            output.Contains("aot-only mode", StringComparison.OrdinalIgnoreCase);
        var unsupportedDynamicCode =
            (output.Contains("System.PlatformNotSupportedException", StringComparison.Ordinal) ||
             output.Contains("System.NotSupportedException", StringComparison.Ordinal)) &&
            (output.Contains("Reflection.Emit is not supported", StringComparison.OrdinalIgnoreCase) ||
             output.Contains("Dynamic code generation is not supported", StringComparison.OrdinalIgnoreCase));

        return liteDbFrame && (executionEngineFailure || unsupportedDynamicCode);
    }

    private static ProcessResult RunProcess(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        int timeoutMilliseconds)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"Could not start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"'{fileName}' exceeded {timeoutMilliseconds / 1000} seconds.");
        }

        Task.WaitAll(stdout, stderr);
        return new ProcessResult(process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string GetMetadata(string key)
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == key).Value ?? string.Empty;
    }

    private static int HarnessFailure(string message)
    {
        Console.Error.WriteLine($"HARNESS_2804_ERROR: {message}");
        return 20;
    }

    private static bool HasMarker(ProcessResult result, string marker)
    {
        return result.Output.Contains(marker, StringComparison.Ordinal);
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
