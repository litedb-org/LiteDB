using System.Reflection;
using System.Text.Json;

using Android.App;
using Android.OS;
using Android.Util;

using LiteDB;
using LiteDB.Tests.Issues;

[Activity(Label = "Issue 2738 regression", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _ = Task.Run(RunCases);
    }

    private void RunCases()
    {
        var outcomes = new List<object>();
        var reportedFailures = 0;
        var unrelatedFailures = 0;
        try
        {
            if (!OperatingSystem.IsAndroid()) throw new InvalidOperationException("Android runtime required");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                foreach (var connection in new[] { ConnectionType.Direct, ConnectionType.Shared })
                foreach (var empty in new[] { false, true })
                {
                    try
                    {
                        new Issue2738_NativeControlTests()
                            .Fresh_encrypted_file_preserves_acknowledged_changes_across_reopens(connection, empty);
                        outcomes.Add(new { attempt, connection = connection.ToString(), startWithEmptyFile = empty, outcome = "passed" });
                    }
                    catch (Exception error)
                    {
                        var reported = error is LiteException && error.Message == "File is not encrypted.";
                        if (reported) reportedFailures++;
                        else unrelatedFailures++;
                        outcomes.Add(new
                        {
                            attempt, connection = connection.ToString(), startWithEmptyFile = empty,
                            outcome = reported ? "reported-failure" : "unrelated-failure", error = error.ToString()
                        });
                    }
                }
            }
        }
        catch (Exception error)
        {
            unrelatedFailures++;
            outcomes.Add(new { outcome = "harness-error", error = error.ToString() });
        }
        var report = new
        {
            android = OperatingSystem.IsAndroid(), androidVersion = Build.VERSION.Release,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            liteDbAssembly = typeof(LiteDatabase).Assembly.FullName,
            build = typeof(MainActivity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(item => item.Key, item => item.Value),
            cases = outcomes.Count, reportedFailures, unrelatedFailures, outcomes
        };
        var json = System.Text.Json.JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(FilesDir.AbsolutePath, "result.json"), json);
        Log.Info("LiteDB2738", "ISSUE_2738_COMPLETE");
    }
}
