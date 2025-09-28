using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Linq;
using LiteDB.ReproRunner.Cli.Execution;
using LiteDB.ReproRunner.Cli.Infrastructure;
using LiteDB.ReproRunner.Cli.Manifests;
using LiteDB.ReproRunner.Shared.Messaging;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace LiteDB.ReproRunner.Cli.Commands;

internal sealed class RunCommand : AsyncCommand<RunCommandSettings>
{
    private const int MaxLogLines = 5;

    private readonly IAnsiConsole _console;
    private readonly ReproRootLocator _rootLocator;
    private readonly RunDirectoryPlanner _planner;
    private readonly ReproBuildCoordinator _buildCoordinator;
    private readonly ReproExecutor _executor;
    private readonly CancellationToken _cancellationToken;
    private readonly ReproOutcomeEvaluator _outcomeEvaluator = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RunCommand"/> class.
    /// </summary>
    /// <param name="console">The console used to render output.</param>
    /// <param name="rootLocator">Resolves the repro root directory.</param>
    /// <param name="planner">Creates deterministic run directories.</param>
    /// <param name="buildCoordinator">Builds repro variants before execution.</param>
    /// <param name="executor">Executes repro variants.</param>
    /// <param name="cancellationToken">Signals cancellation requests.</param>
    public RunCommand(
        IAnsiConsole console,
        ReproRootLocator rootLocator,
        RunDirectoryPlanner planner,
        ReproBuildCoordinator buildCoordinator,
        ReproExecutor executor,
        CancellationToken cancellationToken)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _rootLocator = rootLocator ?? throw new ArgumentNullException(nameof(rootLocator));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _buildCoordinator = buildCoordinator ?? throw new ArgumentNullException(nameof(buildCoordinator));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Executes the run command.
    /// </summary>
    /// <param name="context">The Spectre command context.</param>
    /// <param name="settings">The run settings provided by the user.</param>
    /// <returns>The process exit code.</returns>
    public override async Task<int> ExecuteAsync(CommandContext context, RunCommandSettings settings)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var repository = new ManifestRepository(_rootLocator.ResolveRoot(settings.Root));
        var manifests = repository.Discover();
        var selected = settings.All
            ? manifests.ToList()
            : manifests
                .Where(x => string.Equals(x.Manifest?.Id ?? x.RawId, settings.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (selected.Count == 0)
        {
            if (settings.All)
            {
                _console.MarkupLine("[yellow]No repros discovered.[/]");
                return 0;
            }

            _console.MarkupLine($"[red]Repro '{Markup.Escape(settings.Id!)}' was not found.[/]");
            return 1;
        }

        var report = new RunReport { Root = repository.RootPath };
        var table = new Table().Border(TableBorder.Rounded).Expand().AddColumns("Repro", "State", "Package", "Latest");
        var logLines = new List<string>();
        var targetFps = settings.Fps ?? RunCommandSettings.DefaultFps;
        var layout = new Layout("root")
            .SplitRows(
                new Layout("logs").Size(8),
                new Layout("results"));

        layout["results"].Update(table);
        layout["logs"].Update(CreateLogView(logLines, targetFps));
        var overallExitCode = 0;
        var plannedVariants = new List<RunVariantPlan>();
        var buildFailures = new List<BuildFailure>();
        var uiUpdates = Channel.CreateUnbounded<UiUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        try
        {
            await _console.Live(layout).StartAsync(async ctx =>
            {
                var uiTask = ProcessUiUpdatesAsync(uiUpdates.Reader, table, layout, logLines, targetFps, ctx, _cancellationToken);
                var writer = uiUpdates.Writer;
                var previousObserver = _executor.LogObserver;
                var previousSuppression = _executor.SuppressConsoleLogOutput;
                _executor.SuppressConsoleLogOutput = true;
                _executor.LogObserver = entry =>
                {
                    var formatted = FormatLogLine(entry);
                    writer.TryWrite(new LogLineUpdate(formatted));
                };

                void QueueRow(string reproId, string state, string package, string latest)
                {
                    writer.TryWrite(new TableRowUpdate(reproId, state, package, latest));
                }

                var rowStates = new Dictionary<string, ReproRowState>();

                void UpdateRowState(string reproId, string state, string package, string latest)
                {
                    rowStates[reproId] = new ReproRowState(reproId, state, package, latest);
                    writer.TryWrite(new TableRefreshUpdate(new Dictionary<string, ReproRowState>(rowStates)));
                }

                void LogBuild(string message)
                {
                    writer.TryWrite(new LogLineUpdate($"BUILD: {message}"));
                }

                try
                {
                    var candidates = new List<RunCandidate>();

                    foreach (var repro in selected)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();

                        if (!repro.IsValid)
                        {
                            if (!settings.SkipValidation)
                            {
                                QueueRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Invalid[/]", "[red]❌[/]", "[red]❌[/]");
                                overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                                continue;
                            }

                            if (repro.Manifest is null)
                            {
                                QueueRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Invalid[/]", "[red]❌[/]", "[red]❌[/]");
                                overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                                continue;
                            }
                        }

                        if (repro.Manifest is null)
                        {
                            QueueRow(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]Missing[/]", "[red]❌[/]", "[red]❌[/]");
                            overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                            continue;
                        }

                        var manifest = repro.Manifest;
                        var instances = settings.Instances ?? manifest.DefaultInstances;

                        if (manifest.RequiresParallel && instances < 2)
                        {
                            QueueRow(Markup.Escape(manifest.Id), "[red]Config Error[/]", "[red]❌[/]", "[red]❌[/]");
                            overallExitCode = 1;
                            continue;
                        }

                        if (repro.ProjectPath is null)
                        {
                            QueueRow(Markup.Escape(manifest.Id), "[red]Project Missing[/]", "[red]❌[/]", "[red]❌[/]");
                            overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                            continue;
                        }

                        var timeoutSeconds = settings.Timeout ?? manifest.TimeoutSeconds;
                        var packageVersion = TryResolvePackageVersion(repro.ProjectPath);
                        var packageDisplay = packageVersion ?? "NuGet";
                        var packageVariantId = BuildVariantIdentifier(packageVersion);

                        var packagePlan = _planner.CreateVariantPlan(
                            repro,
                            manifest.Id,
                            packageVariantId,
                            packageDisplay,
                            useProjectReference: false,
                            liteDbPackageVersion: packageVersion);

                        var latestPlan = _planner.CreateVariantPlan(
                            repro,
                            manifest.Id,
                            "ver_latest",
                            "Latest",
                            useProjectReference: true,
                            liteDbPackageVersion: packageVersion);

                        plannedVariants.Add(packagePlan);
                        plannedVariants.Add(latestPlan);

                        var stateCell = FormatReproState(manifest.State);

                        candidates.Add(new RunCandidate(
                            manifest,
                            instances,
                            timeoutSeconds,
                            packageDisplay,
                            packagePlan,
                            latestPlan,
                            stateCell));

                        // Add initial table row showing the repro is discovered and pending
                        UpdateRowState(manifest.Id, stateCell, "[yellow]⏳[/]", "[yellow]⏳[/]");
                        writer.TryWrite(new LogLineUpdate($"Discovered repro: {manifest.Id}"));
                    }

                    if (candidates.Count == 0)
                    {
                        return;
                    }

                    // Update all candidates to show building status
                    foreach (var candidate in candidates)
                    {
                        UpdateRowState(candidate.Manifest.Id, candidate.StateCell, "[yellow]Building...[/]", "[yellow]⏳[/]");
                    }

                    LogBuild($"Starting build for {plannedVariants.Count} variants across {candidates.Count} repros");
                    var buildResults = await _buildCoordinator.BuildAsync(plannedVariants, _cancellationToken).ConfigureAwait(false);
                    LogBuild($"Build completed. Processing {buildResults.Count()} results");
                    var buildLookup = buildResults.ToDictionary(result => result.Plan);

                    foreach (var candidate in candidates)
                    {
                        var stateCell = candidate.StateCell;
                        var packageBuild = buildLookup[candidate.PackagePlan];
                        var latestBuild = buildLookup[candidate.LatestPlan];

                        if (!packageBuild.Succeeded)
                        {
                            LogBuild($"Package build failed for {candidate.Manifest.Id} ({candidate.PackageDisplay})");
                            UpdateRowState(candidate.Manifest.Id, stateCell, "[red]Build Failed[/]", "[red]❌[/]");
                            overallExitCode = overallExitCode == 0 ? 1 : overallExitCode;
                            buildFailures.Add(new BuildFailure(candidate.Manifest.Id, candidate.PackageDisplay, packageBuild.Output));
                        }

                        if (!latestBuild.Succeeded)
                        {
                            LogBuild($"Latest build failed for {candidate.Manifest.Id}");
                            if (packageBuild.Succeeded)
                            {
                                UpdateRowState(candidate.Manifest.Id, stateCell, "[yellow]⏳[/]", "[red]Build Failed[/]");
                            }

                            overallExitCode = overallExitCode == 0 ? 1 : overallExitCode;
                            buildFailures.Add(new BuildFailure(candidate.Manifest.Id, "Latest", latestBuild.Output));
                        }

                        ReproExecutionResult? packageResult = null;
                        ReproExecutionResult? latestResult = null;

                        if (packageBuild.Succeeded)
                        {
                            LogBuild($"Build succeeded for {candidate.Manifest.Id} ({candidate.PackageDisplay}), starting execution");
                            UpdateRowState(candidate.Manifest.Id, stateCell, "[yellow]Running...[/]", latestBuild.Succeeded ? "[yellow]⏳[/]" : "[yellow]⏳[/]");
                            packageResult = await _executor.ExecuteAsync(packageBuild, candidate.Instances, candidate.TimeoutSeconds, _cancellationToken).ConfigureAwait(false);
                        }

                        if (latestBuild.Succeeded)
                        {
                            var interimPackageStatus = packageResult is null
                                ? (packageBuild.Succeeded ? "[yellow]⏳[/]" : "[red]Build Failed[/]")
                                : "[yellow]Completed[/]";
                            UpdateRowState(candidate.Manifest.Id, stateCell, interimPackageStatus, "[yellow]Running...[/]");
                            latestResult = await _executor.ExecuteAsync(latestBuild, candidate.Instances, candidate.TimeoutSeconds, _cancellationToken).ConfigureAwait(false);
                        }

                        var evaluation = _outcomeEvaluator.Evaluate(candidate.Manifest, packageResult, latestResult);
                        var packageCell = FormatVariantCell(evaluation.Package);
                        var latestCell = FormatVariantCell(evaluation.Latest);

                        UpdateRowState(candidate.Manifest.Id, stateCell, packageCell, latestCell);

                        if (evaluation.Package.ShouldFail && evaluation.Package.FailureReason is string packageReason)
                        {
                            writer.TryWrite(new LogLineUpdate($"FAIL: {candidate.Manifest.Id} package - {packageReason}"));
                        }

                        if (evaluation.Latest.ShouldFail && evaluation.Latest.FailureReason is string latestReason)
                        {
                            writer.TryWrite(new LogLineUpdate($"FAIL: {candidate.Manifest.Id} latest - {latestReason}"));
                        }
                        else if (evaluation.Latest.ShouldWarn && evaluation.Latest.FailureReason is string latestWarning)
                        {
                            writer.TryWrite(new LogLineUpdate($"WARN: {candidate.Manifest.Id} latest - {latestWarning}"));
                        }

                        if (evaluation.ShouldFail)
                        {
                            overallExitCode = overallExitCode == 0 ? 1 : overallExitCode;
                        }

                        report.Add(new RunReportEntry
                        {
                            Id = candidate.Manifest.Id,
                            State = candidate.Manifest.State,
                            Failed = evaluation.ShouldFail,
                            Warned = evaluation.ShouldWarn,
                            Package = CreateReportVariant(evaluation.Package, candidate.PackagePlan.UseProjectReference),
                            Latest = CreateReportVariant(evaluation.Latest, candidate.LatestPlan.UseProjectReference)
                        });

                        writer.TryWrite(new LogLineUpdate($"Completed execution for {candidate.Manifest.Id}"));
                    }
                }
                finally
                {
                    _executor.LogObserver = previousObserver;
                    _executor.SuppressConsoleLogOutput = previousSuppression;
                    writer.TryComplete();

                    try
                    {
                        await uiTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }).ConfigureAwait(false);
            if (buildFailures.Count > 0)
            {
                _console.WriteLine();
                foreach (var failure in buildFailures)
                {
                    _console.MarkupLine($"[red]Build failed for {Markup.Escape(failure.ManifestId)} ({Markup.Escape(failure.Variant)}).[/]");

                    if (failure.Output.Count == 0)
                    {
                        _console.MarkupLine("[yellow](No build output captured.)[/]");
                    }
                    else
                    {
                        foreach (var line in failure.Output)
                        {
                            _console.WriteLine(line);
                        }
                    }

                    _console.WriteLine();
                }
            }
        }
        finally
        {
            foreach (var plan in plannedVariants)
            {
                plan.Dispose();
            }
        }

        if (settings.ReportPath is string reportPath)
        {
            await WriteReportAsync(report, reportPath, settings.ReportFormat, _cancellationToken).ConfigureAwait(false);
        }

        return overallExitCode;
    }

    private static async Task WriteReportAsync(RunReport report, string path, string? format, CancellationToken cancellationToken)
    {
        if (format is not null && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported report format '{format}'.");
        }

        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(report, serializerOptions);

        if (string.Equals(path, "-", StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
    }

    private static RunReportVariant CreateReportVariant(ReproVariantEvaluation evaluation, bool useProjectReference)
    {
        var capturedLines = Array.Empty<RunReportCapturedLine>();

        if (evaluation.Result is ReproExecutionResult result)
        {
            capturedLines = result.CapturedOutput
                .Select(line => new RunReportCapturedLine
                {
                    Stream = line.Stream == ReproExecutionStream.StandardOutput ? "stdout" : "stderr",
                    Text = line.Text ?? string.Empty
                })
                .ToArray();
        }

        return new RunReportVariant
        {
            Expected = evaluation.Expectation.Kind,
            ExpectedExitCode = evaluation.Expectation.ExitCode,
            ExpectedLogContains = evaluation.Expectation.LogContains,
            Actual = evaluation.ActualKind,
            Met = evaluation.Met,
            ExitCode = evaluation.Result?.ExitCode,
            DurationSeconds = evaluation.Result?.Duration.TotalSeconds,
            UseProjectReference = evaluation.Result?.UseProjectReference ?? useProjectReference,
            FailureReason = evaluation.FailureReason,
            Output = capturedLines
        };
    }

    private static string FormatVariantCell(ReproVariantEvaluation evaluation)
    {
        var symbol = evaluation.Met
            ? "[green]✅[/]"
            : evaluation.ShouldWarn
                ? "[yellow]⚠️[/]"
                : "[red]❌[/]";

        string detail;
        if (evaluation.Result is ReproExecutionResult result)
        {
            detail = string.Format(CultureInfo.InvariantCulture, "exit {0}", result.ExitCode);
        }
        else
        {
            detail = "no-run";
        }

        var expectation = evaluation.Expectation.Kind switch
        {
            ReproOutcomeKind.Reproduce => "repro",
            ReproOutcomeKind.NoRepro => "no-repro",
            ReproOutcomeKind.HardFail => "hard-fail",
            _ => evaluation.Expectation.Kind.ToString().ToLowerInvariant()
        };

        return $"{symbol} {detail} [dim](exp {expectation})[/]";
    }

    private static string FormatReproState(ReproState state)
    {
        return state switch
        {
            ReproState.Red => "[red]red[/]",
            ReproState.Green => "[green]green[/]",
            ReproState.Flaky => "[yellow]flaky[/]",
            _ => Markup.Escape(state.ToString().ToLowerInvariant())
        };
    }

    private static string BuildVariantIdentifier(string? packageVersion)
    {
        if (string.IsNullOrWhiteSpace(packageVersion))
        {
            return "ver_package";
        }

        var normalized = packageVersion.Replace('.', '_').Replace('-', '_');
        return $"ver_{normalized}";
    }

    private static string? TryResolvePackageVersion(string? projectPath)
    {
        if (projectPath is null || !File.Exists(projectPath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(projectPath);
            var ns = document.Root?.Name.Namespace ?? XNamespace.None;

            var versionElement = document
                .Descendants(ns + "LiteDBPackageVersion")
                .FirstOrDefault();

            if (versionElement is not null && !string.IsNullOrWhiteSpace(versionElement.Value))
            {
                return versionElement.Value.Trim();
            }

            var packageReference = document
                .Descendants(ns + "PackageReference")
                .FirstOrDefault(e => string.Equals(e.Attribute("Include")?.Value, "LiteDB", StringComparison.OrdinalIgnoreCase));

            var version = packageReference?.Attribute("Version")?.Value;
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version!.Trim();
            }
        }
        catch
        {
        }

        return null;
    }

    private sealed record RunCandidate(
        ReproManifest Manifest,
        int Instances,
        int TimeoutSeconds,
        string PackageDisplay,
        RunVariantPlan PackagePlan,
        RunVariantPlan LatestPlan,
        string StateCell);

    private sealed record BuildFailure(string ManifestId, string Variant, IReadOnlyList<string> Output);

    private static IRenderable CreateLogView(IReadOnlyList<string> lines, decimal fps)
    {
        var logTable = new Table().Border(TableBorder.Rounded).Expand();
        var fpsLabel = fps <= 0
            ? "Unlimited"
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0}", fps);
        logTable.AddColumn(new TableColumn($"[bold]Recent Logs[/] [dim](FPS: {fpsLabel})[/]").LeftAligned());

        if (lines.Count == 0)
        {
            logTable.AddRow("[dim]No log entries.[/]");
        }
        else
        {
            foreach (var line in lines)
            {
                logTable.AddRow(line);
            }
        }

        return logTable;
    }

    private static string FormatLogLine(ReproExecutionLogEntry entry)
    {
        var levelMarkup = entry.Level switch
        {
            ReproHostLogLevel.Error or ReproHostLogLevel.Critical => "[red]ERR[/]",
            ReproHostLogLevel.Warning => "[yellow]WRN[/]",
            ReproHostLogLevel.Debug => "[grey]DBG[/]",
            ReproHostLogLevel.Trace => "[grey]TRC[/]",
            _ => "[grey]INF[/]"
        };

        return $"{levelMarkup} [dim]#{entry.InstanceIndex}[/] {Markup.Escape(entry.Message)}";
    }

    private static async Task ProcessUiUpdatesAsync(
        ChannelReader<UiUpdate> reader,
        Table table,
        Layout layout,
        List<string> logLines,
        decimal fps,
        LiveDisplayContext context,
        CancellationToken cancellationToken)
    {
        var refreshInterval = CalculateRefreshInterval(fps);
        var nextRefreshTime = DateTimeOffset.MinValue;
        var needsRefresh = false;

        try
        {
            await foreach (var update in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (update)
                {
                    case LogLineUpdate logUpdate:
                        logLines.Add(logUpdate.Line);
                        while (logLines.Count > MaxLogLines)
                        {
                            logLines.RemoveAt(0);
                        }

                        layout["logs"].Update(CreateLogView(logLines, fps));
                        break;
                    case TableRowUpdate rowUpdate:
                        table.AddRow(rowUpdate.ReproId, rowUpdate.State, rowUpdate.Package, rowUpdate.Latest);
                        break;
                    case TableRefreshUpdate refreshUpdate:
                        // Rebuild the entire table with current states
                        var newTable = new Table().Border(TableBorder.Rounded).Expand().AddColumns("Repro", "State", "Package", "Latest");
                        foreach (var state in refreshUpdate.RowStates.Values.OrderBy(s => s.ReproId))
                        {
                            newTable.AddRow(state.ReproId, state.State, state.Package, state.Latest);
                        }
                        layout["results"].Update(newTable);
                        break;
                }

                needsRefresh = true;

                if (refreshInterval == TimeSpan.Zero || DateTimeOffset.UtcNow >= nextRefreshTime)
                {
                    context.Refresh();
                    needsRefresh = false;

                    if (refreshInterval != TimeSpan.Zero)
                    {
                        nextRefreshTime = DateTimeOffset.UtcNow + refreshInterval;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (needsRefresh)
            {
                context.Refresh();
            }
        }
    }

    private static TimeSpan CalculateRefreshInterval(decimal fps)
    {
        if (fps <= 0)
        {
            return TimeSpan.Zero;
        }

        var secondsPerFrame = (double)(1m / fps);
        return TimeSpan.FromSeconds(secondsPerFrame);
    }

    private abstract record UiUpdate;

    private sealed record LogLineUpdate(string Line) : UiUpdate;

    private sealed record TableRowUpdate(string ReproId, string State, string Package, string Latest) : UiUpdate;

    private sealed record TableRefreshUpdate(Dictionary<string, ReproRowState> RowStates) : UiUpdate;

    private sealed record ReproRowState(string ReproId, string State, string Package, string Latest);
}
