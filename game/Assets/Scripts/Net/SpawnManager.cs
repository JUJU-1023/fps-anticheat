// =====================================================================
//  SpawnManager.cs
//  경로: game/Assets/Scripts/Net/SpawnManager.cs
//
//  서버 전용. 접속한 클라이언트에게 스폰 지점을 순환 배정한다.
//  Bootstrap 씬의 빈 GameObject 에 부착하고 spawnPoints 에 위치들을 연결한다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8.5 : 스폰 Y 를 캐릭터가 실제로 안착하는 높이로 맞춘다 ★
//
//  기존에는 마커 Transform 의 position 을 그대로 돌려줬다. 씬의 마커가
//  y=0 에 놓여 있어 로그에 pos=(-18.48, 0.00, 5.88) 이 찍혔다.
//
//  캐릭터가 실제로 멈추는 높이는 RestY(=1.08)다. 바닥 상면(0)이 아니다.
//  CharacterController 가 접촉면 위 skinWidth 만큼 띄운 채 멈추기 때문이며,
//  계산은 PlayerController.RestY 가 CC 치수에서 유도한다.
//
//  지금은 PlayerController.ServerTeleport 가
//    if (position.y < RestY) position.y = RestY;
//  로 막아주고 있어 증상이 안 나타난다. 그러나 그 경로를 타지 않는
//  코드가 생기면 W7.5 의 "캐릭터가 바닥에 절반 묻히는" 버그가 그대로
//  재현된다. 빌드 프리즈 이후에는 고칠 수 없으므로 여기서 정리한다.
//
//  → GetNextSpawnPosition(PlayerController) 오버로드가 Y 를 그 플레이어의
//    SpawnRestY 로 맞춰 돌려준다. 마커에는 XZ 만 의미가 있다.
//
//  ※ 평평한 지형 1개 전제다. 높이가 다른 층이 생기면 마커별 Y 를 살려야
//    하므로 이 로직을 다시 봐야 한다.
//
//  ─────────────────────────────────────────────────────────────────
//  검증
//
//   마커가 없거나, 하나뿐이거나, 참조가 끊겨 있으면 Start 에서 경고한다.
//   마커가 하나면 두 플레이어가 같은 자리에 겹쳐 스폰되고,
//   끊긴 참조는 런타임 NullReference 로 터진다. 둘 다 조용히 지나가면
//   측정 세션을 통째로 버려야 한다.
// =====================================================================

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class SpawnManager : MonoBehaviour
{
    public static SpawnManager Instance { get; private set; }

    [Tooltip("스폰 마커. Y 는 무시되고 캐릭터의 안착 높이로 대체된다. " +
             "두 개 이상 두어야 플레이어가 겹치지 않는다.")]
    [SerializeField] private List<Transform> spawnPoints = new();

    [Tooltip("마커를 하나도 못 찾았을 때 쓸 좌표. Y 는 마찬가지로 대체된다.")]
    [SerializeField] private Vector3 fallbackPosition = new Vector3(0f, 0f, 0f);

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

        ValidateSpawnPoints();
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
    }

    /// <summary>
    /// 마커 구성을 한 번 점검한다.
    ///
    /// 조용히 넘어가면 측정 세션을 통째로 버리게 되는 종류의 문제들이다.
    /// 마커가 하나면 두 플레이어가 겹쳐 스폰돼 V-LOS/V-TIME 측정이
    /// 무의미해지고, 끊긴 참조는 런타임에 NullReference 로 터진다.
    /// </summary>
    private void ValidateSpawnPoints()
    {
        int nulls = 0;
        foreach (var t in spawnPoints) if (t == null) nulls++;

        if (spawnPoints.Count == 0)
        {
            Debug.LogError(
                "[SPAWN] spawnPoints 가 비어 있습니다. 모든 플레이어가 " +
                $"{fallbackPosition} 한 자리에 겹쳐 스폰됩니다.");
            return;
        }

        if (nulls > 0)
        {
            Debug.LogError(
                $"[SPAWN] spawnPoints 에 빈 참조가 {nulls}개 있습니다. " +
                "Inspector 에서 정리하세요.");
        }

        if (spawnPoints.Count - nulls < 2)
        {
            Debug.LogWarning(
                "[SPAWN] 유효한 마커가 2개 미만입니다. 두 플레이어가 같은 자리에 " +
                "겹쳐 스폰되어 측정이 오염됩니다.");
        }

        Debug.Log($"[SPAWN] 마커 {spawnPoints.Count - nulls}개 확인");
    }

    private void OnClientConnected(ulong clientId)
    {
        // 서버만 위치를 결정한다.
        if (!NetworkManager.Singleton.IsServer) return;

        var netObj = NetworkManager.Singleton.SpawnManager
            .GetPlayerNetworkObject(clientId);

        if (netObj == null)
        {
            Debug.LogWarning(
                $"[SPAWN] client={clientId} 의 플레이어 오브젝트를 찾지 못했습니다. " +
                "원점에 그대로 남습니다.");
            return;
        }

        var pc = netObj.GetComponent<PlayerController>();
        if (pc == null)
        {
            Debug.LogWarning($"[SPAWN] client={clientId} 에 PlayerController 가 없습니다.");
            return;
        }

        pc.ServerTeleport(GetNextSpawnPosition(pc));
    }

    /// <summary>
    /// 다음 스폰 좌표를 돌려준다. Y 는 이 플레이어가 실제로 안착하는
    /// 높이(SpawnRestY)로 맞춘다.
    ///
    /// 마커의 Y 는 쓰지 않는다. 평평한 지형 1개 전제이고, 캐릭터가 멈추는
    /// 높이는 CharacterController 치수에서 유도되므로 마커에 손으로 적어
    /// 두면 CC 를 바꿀 때마다 어긋난다.
    /// </summary>
    public Vector3 GetNextSpawnPosition(PlayerController pc)
    {
        Vector3 p = GetNextMarkerPosition();
        if (pc != null) p.y = pc.SpawnRestY;
        return p;
    }

    /// <summary>
    /// 플레이어를 모를 때 쓰는 버전. Y 는 마커 값 그대로 나가므로
    /// 호출부가 ServerTeleport 를 거쳐야 안착 높이로 보정된다.
    ///
    /// 가능하면 PlayerController 를 넘기는 오버로드를 쓸 것.
    /// </summary>
    public Vector3 GetNextSpawnPosition() => GetNextMarkerPosition();

    private Vector3 GetNextMarkerPosition()
    {
        if (spawnPoints.Count == 0) return fallbackPosition;

        // 빈 참조를 건너뛴다. 한 바퀴를 돌아도 못 찾으면 폴백.
        for (int i = 0; i < spawnPoints.Count; i++)
        {
            Transform t = spawnPoints[nextIndex % spawnPoints.Count];
            nextIndex = (nextIndex + 1) % spawnPoints.Count;   // 오버플로 방지
            if (t != null) return t.position;
        }

        return fallbackPosition;
    }
}