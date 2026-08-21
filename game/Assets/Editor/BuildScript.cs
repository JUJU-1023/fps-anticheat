using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildScript
{
    static string[] Scenes => new[]
    {
        "Assets/Scenes/Bootstrap.unity",
        "Assets/Scenes/Arena.unity"
    };

    [MenuItem("Build/Linux Server x86_64")]
    public static void BuildServer()
    {
        var opts = new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = "Build/Server/GameServer.x86_64",
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"Server build: {report.summary.result} ({report.summary.totalSize} bytes)");
    }

    [MenuItem("Build/Windows Client x86_64")]
    public static void BuildClient()
    {
        var opts = new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = "Build/Client/Game.exe",
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = BuildOptions.None
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"Client build: {report.summary.result}");
    }
}