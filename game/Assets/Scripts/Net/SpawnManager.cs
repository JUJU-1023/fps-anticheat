using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 전용. 접속한 클라이언트에게 스폰 지점을 순환 배정한다.
/// Bootstrap 씬의 빈 GameObject에 부착하고, spawnPoints에 위치들을 연결한다.
/// </summary>
public class SpawnManager : MonoBehaviour
{
    public static SpawnManager Instance { get; private set; }

    [SerializeField] private List<Transform> spawnPoints = new();

    private int nextIndex = 0;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        if (NetworkManager.Singleton == null) return;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
    }

    private void OnClientConnected(ulong clientId)
    {
        // 서버만 위치를 결정한다.
        if (!NetworkManager.Singleton.IsServer) return;

        var netObj = NetworkManager.Singleton.SpawnManager
            .GetPlayerNetworkObject(clientId);
        if (netObj == null) return;

        Vector3 pos = GetNextSpawnPosition();

        var pc = netObj.GetComponent<PlayerController>();
        if (pc != null) pc.ServerTeleport(pos);
    }

    public Vector3 GetNextSpawnPosition()
    {
        if (spawnPoints.Count == 0)
            return new Vector3(0f, 1f, 0f);

        Transform t = spawnPoints[nextIndex % spawnPoints.Count];
        nextIndex++;
        return t.position;
    }
}