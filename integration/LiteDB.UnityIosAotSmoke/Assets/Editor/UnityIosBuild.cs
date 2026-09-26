using System;
using System.IO;

using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class UnityIosBuild
{
    private const string ForceDelegateSymbol = "LITEDB_FORCE_SINGLE_ARGUMENT_DELEGATES";

    public static void Build()
    {
        var target = Environment.GetEnvironmentVariable("LITEDB_UNITY_TARGET") ?? "simulator";
        var simulator = string.Equals(target, "simulator", StringComparison.OrdinalIgnoreCase);
        if (!simulator && !string.Equals(target, "device", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"LITEDB_UNITY_TARGET must be 'simulator' or 'device', not '{target}'.");
        }

        var forceSingleArgumentDelegates = string.Equals(
            Environment.GetEnvironmentVariable("LITEDB_UNITY_FORCE_SINGLE_ARGUMENT_DELEGATES"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        ConfigureDelegateMode(forceSingleArgumentDelegates);

        const string scenePath = "Assets/GeneratedSmoke.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var runner = new GameObject("LiteDB IL2CPP smoke runner");
        runner.AddComponent<LiteDbIl2CppSmoke>();
        EditorSceneManager.SaveScene(scene, scenePath);

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.iOS, ManagedStrippingLevel.High);
        PlayerSettings.iOS.sdkVersion = simulator ? iOSSdkVersion.SimulatorSDK : iOSSdkVersion.DeviceSDK;
        if (simulator)
        {
            PlayerSettings.iOS.simulatorSdkArchitecture = AppleMobileArchitectureSimulator.ARM64;
        }
        PlayerSettings.iOS.targetOSVersionString = "15.0";
        PlayerSettings.applicationIdentifier = "org.litedb.unityiosaotsmoke";
        PlayerSettings.productName = "LiteDB Unity iOS AOT Smoke";
        PlayerSettings.companyName = "LiteDB";

        var configuredOutput = Environment.GetEnvironmentVariable("LITEDB_UNITY_BUILD_PATH");
        if (string.IsNullOrWhiteSpace(configuredOutput))
        {
            throw new InvalidOperationException("LITEDB_UNITY_BUILD_PATH must specify the Xcode export directory.");
        }

        var output = Path.GetFullPath(configuredOutput);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = output,
            target = BuildTarget.iOS,
            options = BuildOptions.None
        });

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Unity iOS IL2CPP build failed: {report.summary.result}, {report.summary.totalErrors} errors.");
        }

        Debug.Log(
            $"LITEDB_UNITY_IOS_BUILD=PASS target={target} forceSingleArgumentDelegates={forceSingleArgumentDelegates} output={output}");
    }

    private static void ConfigureDelegateMode(bool forceSingleArgumentDelegates)
    {
        var symbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.iOS)
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        var filtered = Array.FindAll(symbols, symbol => symbol != ForceDelegateSymbol);
        var configured = forceSingleArgumentDelegates
            ? string.Join(";", filtered) + (filtered.Length == 0 ? string.Empty : ";") + ForceDelegateSymbol
            : string.Join(";", filtered);
        PlayerSettings.SetScriptingDefineSymbolsForGroup(BuildTargetGroup.iOS, configured);
    }
}
