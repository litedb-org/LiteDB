using System.Diagnostics;
using System.Text;

namespace LiteDB.ReproRunner.Cli.Execution;

/// <summary>
/// Coordinates building repro variants ahead of execution.
/// </summary>
internal sealed class ReproBuildCoordinator
{
    /// <summary>
    /// Builds the provided repro variants sequentially.
    /// </summary>
    /// <param name="variants">The variants to build.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>The collection of build results for the supplied variants.</returns>
    public async Task<IReadOnlyList<ReproBuildResult>> BuildAsync(
        IEnumerable<RunVariantPlan> variants,
        CancellationToken cancellationToken)
    {
        if (variants is null)
        {
            throw new ArgumentNullException(nameof(variants));
        }

        var results = new List<ReproBuildResult>();

        foreach (var plan in variants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (plan.Repro.ProjectPath is null)
            {
                results.Add(ReproBuildResult.CreateMissingProject(plan));
                continue;
            }

            var projectPath = plan.Repro.ProjectPath;
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var assemblyName = Path.GetFileNameWithoutExtension(projectPath);

            var arguments = new List<string>
            {
                "build",
                projectPath,
                "-c",
                "Release",
                "--nologo",
                $"-p:UseProjectReference={(plan.UseProjectReference ? "true" : "false")}",
                $"-p:OutputPath={plan.BuildOutputDirectory}"
            };

            if (!string.IsNullOrWhiteSpace(plan.LiteDBPackageVersion))
            {
                arguments.Add($"-p:LiteDBPackageVersion={plan.LiteDBPackageVersion}");
            }

            var (exitCode, output) = await RunProcessAsync(projectDirectory, arguments, cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                results.Add(ReproBuildResult.CreateFailure(plan, exitCode, output));
                continue;
            }

            var assemblyPath = Path.Combine(plan.BuildOutputDirectory, assemblyName + ".dll");

            if (!File.Exists(assemblyPath))
            {
                output.Add($"Assembly '{assemblyPath}' was not produced by build.");
                results.Add(ReproBuildResult.CreateFailure(plan, exitCode: -1, output));
                continue;
            }

            results.Add(ReproBuildResult.CreateSuccess(plan, assemblyPath, output));
        }

        return results;
    }

    private static async Task<(int ExitCode, List<string> Output)> RunProcessAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet build process.");

        var lines = new List<string>();

        var outputTask = CaptureAsync(process.StandardOutput, lines, cancellationToken);
        var errorTask = CaptureAsync(process.StandardError, lines, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

        return (process.ExitCode, lines);
    }

    private static async Task CaptureAsync(StreamReader reader, List<string> destination, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                destination.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// Represents the outcome of a repro variant build.
/// </summary>
internal sealed class ReproBuildResult
{
    private ReproBuildResult(
        RunVariantPlan plan,
        bool succeeded,
        int exitCode,
        string? assemblyPath,
        IReadOnlyList<string> output)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Succeeded = succeeded;
        ExitCode = exitCode;
        AssemblyPath = assemblyPath;
        Output = output;
    }

    /// <summary>
    /// Gets the plan that was built.
    /// </summary>
    public RunVariantPlan Plan { get; }

    /// <summary>
    /// Gets a value indicating whether the build succeeded.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the exit code reported by the build process.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the path to the built assembly when the build succeeds.
    /// </summary>
    public string? AssemblyPath { get; }

    /// <summary>
    /// Gets the captured build output lines.
    /// </summary>
    public IReadOnlyList<string> Output { get; }

    /// <summary>
    /// Creates a successful build result for the specified plan.
    /// </summary>
    /// <param name="plan">The plan that was built.</param>
    /// <param name="assemblyPath">The path to the produced assembly.</param>
    /// <param name="output">The captured output from the build process.</param>
    /// <returns>The successful build result.</returns>
    public static ReproBuildResult CreateSuccess(RunVariantPlan plan, string assemblyPath, IReadOnlyList<string> output)
    {
        return new ReproBuildResult(plan, succeeded: true, exitCode: 0, assemblyPath: assemblyPath, output: output);
    }

    /// <summary>
    /// Creates a failed build result for the specified plan.
    /// </summary>
    /// <param name="plan">The plan that was built.</param>
    /// <param name="exitCode">The exit code returned by the build process.</param>
    /// <param name="output">The captured output from the build process.</param>
    /// <returns>The failed build result.</returns>
    public static ReproBuildResult CreateFailure(RunVariantPlan plan, int exitCode, IReadOnlyList<string> output)
    {
        return new ReproBuildResult(plan, succeeded: false, exitCode: exitCode, assemblyPath: null, output: output);
    }

    /// <summary>
    /// Creates a build result indicating that the repro project was not found.
    /// </summary>
    /// <param name="plan">The plan that failed to build.</param>
    /// <returns>The build result representing the missing project.</returns>
    public static ReproBuildResult CreateMissingProject(RunVariantPlan plan)
    {
        var output = new List<string> { "No project file was discovered for this repro." };
        return new ReproBuildResult(plan, succeeded: false, exitCode: -2, assemblyPath: null, output);
    }
}
