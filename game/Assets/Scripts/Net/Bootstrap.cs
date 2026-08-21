using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

public class Bootstrap : MonoBehaviour
{
    [SerializeField] GameObject connectUI;
    [SerializeField] Camera lobbyCamera;

    void Start()
    {
        var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
        var cfg = NetConfig.Instance;

#if UNITY_SERVER
        utp.SetConnectionData(cfg.listenAddress, cfg.port, cfg.listenAddress);
        NetworkManager.Singleton.StartServer();
        GameLog.Info("SERVER_START");                                    // ← 추가
        if (connectUI) connectUI.SetActive(false);
#else
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-batchmode") >= 0)
        {
            utp.SetConnectionData(cfg.listenAddress, cfg.port, cfg.listenAddress);
            NetworkManager.Singleton.StartServer();
            GameLog.Info("SERVER_START");
            return;
        }
        if (connectUI) connectUI.SetActive(true);
#endif
    }

    public void OnClickConnect(string address)
    {
        var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
        var cfg = NetConfig.Instance;
        var target = string.IsNullOrEmpty(address) ? cfg.serverAddress : address;
        utp.SetConnectionData(target, cfg.port);
        NetworkManager.Singleton.StartClient();
        DisableLobbyCamera();
        GameLog.Info("CLIENT_CONNECT");                                  // ← 교체
    }

    public void DisableLobbyCamera()
    {
        if (lobbyCamera) lobbyCamera.gameObject.SetActive(false);
    }
}