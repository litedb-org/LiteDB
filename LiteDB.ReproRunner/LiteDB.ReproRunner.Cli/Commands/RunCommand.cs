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
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Expand()
            .AddColumns("Repro", "Repro Version", "Reproduced", "Fixed", "Overall");
        var overallExitCode = 0;
        var plannedVariants = new List<RunVariantPlan>();
        var buildFailures = new List<BuildFailure>();
        var useLiveDisplay = ShouldUseLiveDisplay();

        try
        {
            if (useLiveDisplay)
            {
                var logLines = new List<string>();
                var targetFps = settings.Fps ?? RunCommandSettings.DefaultFps;
                var layout = new Layout("root")
                    .SplitRows(
                        new Layout("logs").Size(8),
                        new Layout("results"));

                layout["results"].Update(table);
                layout["logs"].Update(CreateLogView(logLines, targetFps));

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
                        var rowStates = new Dictionary<string, ReproRowState>();
                        var previousObserver = _executor.LogObserver;
                        var previousSuppression = _executor.SuppressConsoleLogOutput;
                        _executor.SuppressConsoleLogOutput = true;
                        _executor.LogObserver = entry =>
                        {
                            var formatted = FormatLogLine(entry);
                            writer.TryWrite(new LogLineUpdate(formatted));
                        };

                        void HandleInitialRow(ReproRowState state)
                        {
                            rowStates[state.ReproId] = state;
                            writer.TryWrite(new TableRowUpdate(state.ReproId, state.ReproVersion, state.Reproduced, state.Fixed, state.Overall));
                        }

                        void HandleRowUpdate(ReproRowState state)
                        {
                            rowStates[state.ReproId] = state;
                            writer.TryWrite(new TableRefreshUpdate(new Dictionary<string, ReproRowState>(rowStates)));
                        }

                        void LogLine(string message)
                        {
                            writer.TryWrite(new LogLineUpdate(message));
                        }

                        void LogBuild(string message)
                        {
                            writer.TryWrite(new LogLineUpdate($"BUILD: {message}"));
                        }

                        try
                        {
                            var result = await RunExecutionLoopAsync(
                                selected,
                                settings,
                                report,
                                plannedVariants,
                                buildFailures,
                                HandleInitialRow,
                                HandleRowUpdate,
                                LogLine,
                                LogBuild,
                                _cancellationToken).ConfigureAwait(false);
                            overallExitCode = result.ExitCode;
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
                }
                finally
                {
                    uiUpdates.Writer.TryComplete();
                }
            }
            else
            {
                var previousObserver = _executor.LogObserver;
                var previousSuppression = _executor.SuppressConsoleLogOutput;
                RunExecutionResult result;

                try
                {
                    _executor.SuppressConsoleLogOutput = true;
                    _executor.LogObserver = entry =>
                    {
                        var formatted = FormatLogLine(entry);
                        _console.MarkupLine(formatted);
                    };

                    result = await RunExecutionLoopAsync(
                        selected,
                        settings,
                        report,
                        plannedVariants,
                        buildFailures,
                        _ => { },
                        _ => { },
                        message => _console.MarkupLine(message),
                        message => _console.MarkupLine($"BUILD: {Markup.Escape(message)}"),
                        _cancellationToken).ConfigureAwait(false);
                    overallExitCode = result.ExitCode;
                }
                finally
                {
                    _executor.LogObserver = previousObserver;
                    _executor.SuppressConsoleLogOutput = previousSuppression;
                }

                _console.WriteLine();
                var finalTable = new Table()
                    .Border(TableBorder.Rounded)
                    .Expand()
                    .AddColumns("Repro", "Repro Version", "Reproduced", "Fixed", "Overall");

                foreach (var state in result.States.Values.OrderBy(s => s.ReproId))
                {
                    finalTable.AddRow(state.ReproId, state.ReproVersion, state.Reproduced, state.Fixed, state.Overall);
                }

                _console.Write(finalTable);
            }

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


    private async Task<RunExecutionResult> RunExecutionLoopAsync(
        IReadOnlyList<DiscoveredRepro> selected,
        RunCommandSettings settings,
        RunReport report,
        List<RunVariantPlan> plannedVariants,
        List<BuildFailure> buildFailures,
        Action<ReproRowState> onInitialRow,
        Action<ReproRowState> onRowStateUpdated,
        Action<string> logLine,
        Action<string> logBuild,
        CancellationToken cancellationToken)
    {
        var overallExitCode = 0;
        var candidates = new List<RunCandidate>();
        var finalStates = new Dictionary<string, ReproRowState>();

        foreach (var repro in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!repro.IsValid)
            {
                if (!settings.SkipValidation)
                {
                    var state = new ReproRowState(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]n/a[/]", "[red]❌[/]", "[red]❌[/]", "[red]Invalid[/]");
                    onInitialRow(state);
                    finalStates[state.ReproId] = state;
                    overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                    continue;
                }

                if (repro.Manifest is null)
                {
                    var state = new ReproRowState(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]n/a[/]", "[red]❌[/]", "[red]❌[/]", "[red]Invalid[/]");
                    onInitialRow(state);
                    finalStates[state.ReproId] = state;
                    overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                    continue;
                }
            }

            if (repro.Manifest is null)
            {
                var state = new ReproRowState(Markup.Escape(repro.RawId ?? "(unknown)"), "[red]n/a[/]", "[red]❌[/]", "[red]❌[/]", "[red]Missing[/]");
                onInitialRow(state);
                finalStates[state.ReproId] = state;
                overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                continue;
            }

            var manifest = repro.Manifest;
            var instances = settings.Instances ?? manifest.DefaultInstances;

            if (manifest.RequiresParallel && instances < 2)
            {
                var state = new ReproRowState(Markup.Escape(manifest.Id), "[red]n/a[/]", "[red]❌[/]", "[red]❌[/]", "[red]Config Error[/]");
                onInitialRow(state);
                finalStates[state.ReproId] = state;
                overallExitCode = 1;
                continue;
            }

            if (repro.ProjectPath is null)
            {
                var state = new ReproRowState(Markup.Escape(manifest.Id), "[red]n/a[/]", "[red]❌[/]", "[red]❌[/]", "[red]Project Missing[/]");
                onInitialRow(state);
                finalStates[state.ReproId] = state;
                overallExitCode = overallExitCode == 0 ? 2 : overallExitCode;
                continue;
            }

            var timeoutSeconds = settings.Timeout ?? manifest.TimeoutSeconds;
            var packageVersion = TryResolvePackageVersion(repro.ProjectPath);
            var packageDisplay = packageVersion ?? "NuGet";
            var packageVariantId = BuildVariantIdentifier(packageVersion);
            var failingSince = string.IsNullOrWhiteSpace(manifest.FailingSince)
                ? packageDisplay
                : manifest.FailingSince!;
            var reproVersionCell = Markup.Escape(failingSince);
            var pendingOverallCell = FormatOverallPending(manifest.State);

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

            var candidate = new RunCandidate(
                manifest,
                instances,
                timeoutSeconds,
                packageDisplay,
                packagePlan,
                latestPlan,
                reproVersionCell);

            candidates.Add(candidate);

            var pendingState = new ReproRowState(manifest.Id, reproVersionCell, "[yellow]⏳[/]", "[yellow]⏳[/]", pendingOverallCell);
            onRowStateUpdated(pendingState);
            finalStates[manifest.Id] = pendingState;

            logLine($"Discovered repro: {Markup.Escape(manifest.Id)}");
        }

        if (candidates.Count == 0)
        {
            return new RunExecutionResult(overallExitCode, finalStates);
        }

        foreach (var candidate in candidates)
        {
            var buildingState = new ReproRowState(
                candidate.Manifest.Id,
                candidate.ReproVersionCell,
                "[yellow]Building...[/]",
                "[yellow]⏳[/]",
                FormatOverallPending(candidate.Manifest.State));
            onRowStateUpdated(buildingState);
            finalStates[candidate.Manifest.Id] = buildingState;
        }

        logBuild($"Starting build for {plannedVariants.Count} variants across {candidates.Count} repros");
        var buildResults = await _buildCoordinator.BuildAsync(plannedVariants, cancellationToken).ConfigureAwait(false);
        logBuild($"Build completed. Processing {buildResults.Count()} results");
        var buildLookup = buildResults.ToDictionary(result => result.Plan);

        foreach (var candidate in candidates)
        {
            var packageBuild = buildLookup[candidate.PackagePlan];
            var latestBuild = buildLookup[candidate.LatestPlan];

            if (!packageBuild.Succeeded)
            {
                logBuild($"Package build failed for {Markup.Escape(candidate.Manifest.Id)} ({Markup.Escape(candidate.PackageDisplay)})");
                var failedState = new ReproRowState(candidate.Manifest.Id, candidate.ReproVersionCell, "[red]Build Failed[/]", "[red]❌[/]", "[red]Build Failed[/]");
                onRowStateUpdated(failedState);
                finalStates[candidate.Manifest.Id] = failedState;
                overallExitCode = overallExitCode == 0 ? 1 : overallExitCode;
                buildFailures.Add(new BuildFailure(candidate.Manifest.Id, candidate.PackageDisplay, packageBuild.Output));
            }

            if (!latestBuild.Succeeded)
            {
                logBuild($"Latest build failed for {Markup.Escape(candidate.Manifest.Id)}");
                if (packageBuild.Succeeded)
                {
                    var partialState = new ReproRowState(candidate.Manifest.Id, candidate.ReproVersionCell, "[yellow]⏳[/]", "[red]Build Failed[/]", "[red]Build Failed[/]");
                    onRowStateUpdated(partialState);
                    finalStates[candidate.Manifest.Id] = partialState;
                }

                overallExitCode = overallExitCode == 0 ? 1 : overallExitCode;
                buildFailures.Add(new BuildFailure(candidate.Manifest.Id, "Latest", latestBuild.Output));
            }

            ReproExecutionResult? packageResult = null;
            ReproExecutionResult? latestResult = null;

            if (packageBuild.Succeeded)
            {
                logBuild($"Build succeeded for {Markup.Escape(candidate.Manifest.Id)} ({Markup.Escape(candidate.PackageDisplay)}), starting execution");
                var runningState = new ReproRowState(candidate.Manifest.Id, candidate.ReproVersionCell, "[yellow]Running...[/]", latestBuild.Succeeded ? "[yellow]⏳[/]" : "[yellow]⏳[/]", FormatOverallPending(candidate.Manifest.State));
                onRowStateUpdated(runningState);
                finalStates[candidate.Manifest.Id] = runningState;
                packageResult = await _executor.ExecuteAsync(packageBuild, candidate.Instances, candidate.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
            }

            if (latestBuild.Succeeded)
            {
                var interimPackageStatus = packageResult is null
                    ? (packageBuild.Succeeded ? "[yellow]⏳[/]" : "[red]Build Failed[/]")
                    : "[yellow]Completed[/]";
                var latestRunningState = new ReproRowState(candidate.Manifest.Id, candidate.ReproVersionCell, interimPackageStatus, "[yellow]Running...[/]", FormatOverallPending(candidate.Manifest.State));
                onRowStateUpdated(latestRunningState);
                finalStates[candidate.Manifest.Id] = latestRunningState;
                latestResult = await _executor.ExecuteAsync(latestBuild, candidate.Instances, candidate.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
            }

            var evaluation = _outcomeEvaluator.Evaluate(candidate.Manifest, packageResult, latestResult);
            var packageCell = FormatVariantCell(evaluation.Package);
            var latestCell = FormatVariantCell(evaluation.Latest, false);
            var overallState = ComputeOverallState(evaluation);
            var overallCell = FormatOverallCell(evaluation, overallState);
            var finalState = new ReproRowState(candidate.Manifest.Id, candidate.ReproVersionCell, packageCell, latestCell, overallCell);
            onRowStateUpdated(finalState);
            finalStates[candidate.Manifest.Id] = finalState;

            if (evaluation.Package.ShouldFail && evaluation.Package.FailureReason is string packageReason)
            {
                logLine($"FAIL: {Markup.Escape(candidate.Manifest.Id)} package - {Markup.Escape(packageReason)}");
            }

            if (evaluation.Latest.ShouldFail && evaluation.Latest.FailureReason is string latestReason)
            {
                logLine($"FAIL: {Markup.Escape(candidate.Manifest.Id)} latest - {Markup.Escape(latestReason)}");
            }
            else if (evaluation.Latest.ShouldWarn && evaluation.Latest.FailureReason is string latestWarning)
            {
                logLine($"WARN: {Markup.Escape(candidate.Manifest.Id)} latest - {Markup.Escape(latestWarning)}");
            }

            if (evaluation.ShouldFail)
            {
                overallExitCode = overallExitCode == 0 ? 1 : overallExitCode;
            }

            report.Add(new RunReportEntry
            {
                Id = candidate.Manifest.Id,
                State = overallState,
                Failed = evaluation.ShouldFail,
                Warned = evaluation.ShouldWarn,
                Package = CreateReportVariant(evaluation.Package, candidate.PackagePlan.UseProjectReference),
                Latest = CreateReportVariant(evaluation.Latest, candidate.LatestPlan.UseProjectReference)
            });

            logLine($"Completed execution for {Markup.Escape(candidate.Manifest.Id)}");
        }

        return new RunExecutionResult(overallExitCode, finalStates);
    }

    private bool ShouldUseLiveDisplay()
    {
        if (IsCiEnvironment())
        {
            return false;
        }

        return _console.Profile.Capabilities.Interactive;
    }

    private static bool IsCiEnvironment()
    {
        static bool IsTrue(string? value) => !string.IsNullOrEmpty(value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        return IsTrue(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")) || IsTrue(Environment.GetEnvironmentVariable("CI"));
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

    private static string FormatVariantCell(ReproVariantEvaluation evaluation, bool judgeOnMet = true)
    {
        var symbol1 = evaluation.Result?.Reproduced switch
        {
            true => "[green]✅[/]",
            false => "[red]❌[/]",
            null => "[yellow]⚠️[/]"
        };

        return FormatVariantCell(evaluation, symbol1);
    }

    private static string FormatVariantCell(ReproVariantEvaluation evaluation, string symbol)
    {
        var detail = evaluation.Result is { } result 
            ? string.Format(CultureInfo.InvariantCulture, "exit {0}", result.ExitCode) 
            : "no-run";

        var expectation = evaluation.Expectation.Kind switch
        {
            ReproOutcomeKind.Reproduce => "repro",
            ReproOutcomeKind.NoRepro => "no-repro",
            ReproOutcomeKind.HardFail => "hard-fail",
            _ => evaluation.Expectation.Kind.ToString().ToLowerInvariant()
        };

        return $"{symbol} {detail} [dim](exp {expectation})[/]";
    }

    private static ReproState ComputeOverallState(ReproRunEvaluation evaluation)
    {
        if (evaluation.ShouldFail)
        {
            return ReproState.Red;
        }

        if (evaluation.ShouldWarn)
        {
            return ReproState.Flaky;
        }

        return ReproState.Green;
    }

    private static string FormatOverallCell(ReproRunEvaluation evaluation, ReproState overallState)
    {
        var symbol = evaluation.ShouldFail
            ? "[red]❌[/]"
            : evaluation.ShouldWarn
                ? "[yellow]⚠️[/]"
                : "[green]✅[/]";

        return $"{symbol} {FormatReproState(overallState)}";
    }

    private static string FormatOverallPending(ReproState state)
    {
        return $"[yellow]⏳[/] {FormatReproState(state)}";
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

    private sealed record RunExecutionResult(int ExitCode, Dictionary<string, ReproRowState> States);

    private sealed record RunCandidate(
        ReproManifest Manifest,
        int Instances,
        int TimeoutSeconds,
        string PackageDisplay,
        RunVariantPlan PackagePlan,
        RunVariantPlan LatestPlan,
        string ReproVersionCell);

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
                        table.AddRow(rowUpdate.ReproId, rowUpdate.ReproVersion, rowUpdate.Reproduced, rowUpdate.Fixed, rowUpdate.Overall);
                        break;
                    case TableRefreshUpdate refreshUpdate:
                        // Rebuild the entire table with current states
                        var newTable = new Table()
                            .Border(TableBorder.Rounded)
                            .Expand()
                            .AddColumns("Repro", "Repro Version", "Reproduced", "Fixed", "Overall");
                        foreach (var state in refreshUpdate.RowStates.Values.OrderBy(s => s.ReproId))
                        {
                            newTable.AddRow(state.ReproId, state.ReproVersion, state.Reproduced, state.Fixed, state.Overall);
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

    private sealed record TableRowUpdate(string ReproId, string ReproVersion, string Reproduced, string Fixed, string Overall) : UiUpdate;

    private sealed record TableRefreshUpdate(Dictionary<string, ReproRowState> RowStates) : UiUpdate;

    private sealed record ReproRowState(string ReproId, string ReproVersion, string Reproduced, string Fixed, string Overall);
}
