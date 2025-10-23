using System;
using System.Linq;

namespace LiteDB.Tests;

public class Program
{
    public static int Main(string[] args)
    {
        try
        {
            // Handle cross-process worker mode
            if (args.Contains("--crossprocess-worker"))
            {
                var dbPath = GetArgValue(args, "--db");
                var processId = int.Parse(GetArgValue(args, "--process-id"));
                var docCount = int.Parse(GetArgValue(args, "--doc-count"));

                Engine.CrossProcess_Shared_Tests.CrossProcessWorker(dbPath, processId, docCount);
                return 0;
            }

            // Handle cross-process counter mode
            if (args.Contains("--crossprocess-counter"))
            {
                var dbPath = GetArgValue(args, "--db");
                var processId = int.Parse(GetArgValue(args, "--process-id"));
                var operationCount = int.Parse(GetArgValue(args, "--operation-count"));

                Engine.CrossProcess_Shared_Tests.CrossProcessCounter(dbPath, processId, operationCount);
                return 0;
            }

            // Default: run as normal test assembly
            Console.WriteLine("LiteDB.Tests - use dotnet test to run tests");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static string GetArgValue(string[] args, string argName)
    {
        var index = Array.IndexOf(args, argName);
        if (index == -1 || index + 1 >= args.Length)
        {
            throw new ArgumentException($"Argument {argName} not found or has no value");
        }
        return args[index + 1];
    }
}
