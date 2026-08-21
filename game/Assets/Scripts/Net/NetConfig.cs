using System;
using System.IO;
using UnityEngine;

[Serializable]
public class NetConfig
{
    public string serverAddress = "127.0.0.1";
    public ushort port = 7777;
    public string listenAddress = "0.0.0.0";

    static NetConfig _instance;
    public static NetConfig Instance => _instance ??= Load();

    static NetConfig Load()
    {
        var cfg = new NetConfig();

        // 1) 커맨드라인 인자 우선 (서버 운영용)
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-serverAddress") cfg.serverAddress = args[i + 1];
            if (args[i] == "-port" && ushort.TryParse(args[i + 1], out var p)) cfg.port = p;
        }

        // 2) config.json (클라이언트 배포용)
        var path = Path.Combine(Application.streamingAssetsPath, "config.json");
        if (File.Exists(path))
        {
            try { JsonUtility.FromJsonOverwrite(File.ReadAllText(path), cfg); }
            catch (Exception e) { Debug.LogWarning($"config.json parse failed: {e.Message}"); }
        }
        return cfg;
    }
}