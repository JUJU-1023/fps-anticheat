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
        // 실제 Linux Dedicated Server 빌드에서만 이 분기를 탄다.
        utp.SetConnectionData(cfg.listenAddress, cfg.port, cfg.listenAddress);
        NetworkManager.Singleton.StartServer();
        GameLog.Info("SERVER_START");
        if (connectUI) connectUI.SetActive(false);
#else
        // 에디터(MPPM 가상 플레이어 포함) 및 Windows 클라이언트 빌드.
        // -batchmode 판정을 제거했다: MPPM 가상 플레이어가 batchmode로 뜨면서
        // 서버 분기를 타고 7777 포트를 중복 바인드하는 문제가 있었음.
        if (connectUI) connectUI.SetActive(true);
#endif
    }

    /// <summary>에디터에서 서버 겸 플레이어로 시작 (테스트용)</summary>
    public void OnClickHost()
    {
        var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
        var cfg = NetConfig.Instance;

        utp.SetConnectionData("127.0.0.1", cfg.port, "0.0.0.0");
        NetworkManager.Singleton.StartHost();
        if (connectUI) connectUI.SetActive(false);
        DisableLobbyCamera();
        GameLog.Info("HOST_START");
    }

    public void OnClickConnect(string address)
    {
        var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
        var cfg = NetConfig.Instance;

        var target = string.IsNullOrEmpty(address) ? cfg.serverAddress : address;
        utp.SetConnectionData(target, cfg.port);
        NetworkManager.Singleton.StartClient();
        if (connectUI) connectUI.SetActive(false);
        DisableLobbyCamera();
        GameLog.Info("CLIENT_CONNECT");
    }

    public void DisableLobbyCamera()
    {
        if (lobbyCamera) lobbyCamera.gameObject.SetActive(false);
    }
}