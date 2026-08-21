using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

public class Bootstrap : MonoBehaviour
{
    [SerializeField] GameObject connectUI;

    void Start()
    {
        var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
        var cfg = NetConfig.Instance;

#if UNITY_SERVER
        utp.SetConnectionData(cfg.listenAddress, cfg.port, cfg.listenAddress);
        NetworkManager.Singleton.StartServer();
        Debug.Log($"[SERVER] listening on {cfg.listenAddress}:{cfg.port}");
        if (connectUI) connectUI.SetActive(false);
#else
        // 헤드리스(-batchmode)로 실행된 경우도 서버로 취급 (에디터 테스트 대비)
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-batchmode") >= 0)
        {
            utp.SetConnectionData(cfg.listenAddress, cfg.port, cfg.listenAddress);
            NetworkManager.Singleton.StartServer();
            Debug.Log($"[SERVER] listening on {cfg.listenAddress}:{cfg.port}");
            return;
        }
        if (connectUI) connectUI.SetActive(true);
#endif
    }

    public void OnClickConnect(string address)
    {
        var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
        var cfg = NetConfig.Instance;
        utp.SetConnectionData(
            string.IsNullOrEmpty(address) ? cfg.serverAddress : address,
            cfg.port);
        NetworkManager.Singleton.StartClient();
        Debug.Log($"[CLIENT] connecting to {(string.IsNullOrEmpty(address) ? cfg.serverAddress : address)}:{cfg.port}");
    }
}