using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_2163_MixedAccess;

internal static class Program
{
    private const int HarnessFailureExitCode = 20;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var paths = ScenarioPaths.Create(context.SharedDatabaseRoot);

        host.SendLifecycle("starting", new
        {
            context.InstanceIndex,
            context.TotalInstances,
            context.SharedDatabaseRoot
        });

        try
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Issue 2163 requires modern Linux file-sharing semantics.");
            }

            if (context.TotalInstances != 2 || context.InstanceIndex is < 0 or > 1)
            {
                throw new InvalidOperationException(
                    $"Issue 2163 requires exactly two processes; received index " +
                    $"{context.InstanceIndex} of {context.TotalInstances}.");
            }

            var verdict = context.InstanceIndex == 0
                ? OwnerScenario.Run(host, paths)
                : ContenderScenario.Run(host, paths);

            Console.WriteLine(verdict.Marker);
            host.SendResult(verdict.ExitCode == 0, verdict.Marker, new { verdict.Summary });
            host.SendLifecycle("completed", new { Success = verdict.ExitCode == 0, verdict.ExitCode });
            return verdict.ExitCode;
        }
        catch (Exception ex)
        {
            if (context.InstanceIndex == 0)
            {
                Coordination.TryWriteJson(paths.Verdict, Verdict.HarnessFailure(ex.ToString()));
                paths.TryDeleteDatabaseAlias();
            }
            else
            {
                Coordination.TryWriteJson(paths.ContenderFatal, ExceptionDetails.Capture(ex));
            }

            host.SendLog($"Issue 2163 harness failed: {ex}", ReproHostLogLevel.Error);
            host.SendResult(false, "HARNESS_ERROR_2163", new { Exception = ex.ToString() });
            host.SendLifecycle("completed", new { Success = false, ExitCode = HarnessFailureExitCode });
            Console.Error.WriteLine(ex);
            return HarnessFailureExitCode;
        }
    }
}
