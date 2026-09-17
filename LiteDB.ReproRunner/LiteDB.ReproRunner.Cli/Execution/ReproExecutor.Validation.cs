namespace LiteDB.ReproRunner.Cli.Execution;

internal sealed partial class ReproExecutor
{
    internal bool ValidateConfiguration()
    {
        FinalizeConfigurationValidation();
        // Keep harness validity separate: every Int32 is a valid child exit.
        return !HasConfigurationMismatch();
    }

    internal static int AggregateExitCodes(System.Collections.Generic.IEnumerable<int> exits)
    {
        var result = 10;
        var any = false;
        foreach (var exit in exits)
        {
            // Harness failures take precedence over completed observations.
            // Otherwise any reproduction wins over verification, so a latest
            // regression cannot be hidden by another successful instance.
            if (exit != 0 && exit != 10) return exit;
            if (exit == 0) result = 0;
            any = true;
        }
        return any ? result : 0;
    }
}
