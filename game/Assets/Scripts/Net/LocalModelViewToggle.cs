using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 1인칭 시점에서 본인 3인칭 모델을 숨긴다.
/// 렌더러를 끄지 않고 ShadowsOnly 로 바꿔 자기 그림자는 남긴다.
/// 레이어는 건드리지 않는다 — Player 레이어는 랙 보상 히트스캔이 의존한다.
/// </summary>
public class LocalModelViewToggle : NetworkBehaviour
{
    [Header("대상")]
    [Tooltip("3인칭 캐릭터 모델의 루트. 반드시 지정할 것.")]
    [SerializeField] private Transform modelRoot;

    [Tooltip("이 하위 트리는 건드리지 않는다 (1인칭 무기 등).")]
    [SerializeField] private Transform[] excludeRoots;

    [Header("진단")]
    [SerializeField] private bool logOnSpawn = true;

    private readonly List<Renderer> _targets = new List<Renderer>();

    public override void OnNetworkSpawn()
    {
        // 원격 플레이어 모델은 그대로 보여야 한다
        if (!IsOwner) return;

        if (modelRoot == null)
        {
            Debug.LogWarning("[LocalModelView] modelRoot 미지정 — 루트 전체를 검색합니다. " +
                             "무기 렌더러까지 숨을 수 있으니 지정을 권장합니다.");
        }

        Collect();
        Apply(ShadowCastingMode.ShadowsOnly);

        if (logOnSpawn)
            Debug.Log($"[LocalModelView] ShadowsOnly 적용 {_targets.Count}개 " +
                      $"(clientId={OwnerClientId})");
    }

    private void Collect()
    {
        _targets.Clear();

        Transform root = modelRoot != null ? modelRoot : transform;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (IsExcluded(r.transform)) continue;
            _targets.Add(r);
        }
    }

    private bool IsExcluded(Transform t)
    {
        if (excludeRoots == null) return false;

        foreach (Transform ex in excludeRoots)
        {
            if (ex == null) continue;
            if (t == ex || t.IsChildOf(ex)) return true;
        }
        return false;
    }

    private void Apply(ShadowCastingMode mode)
    {
        foreach (Renderer r in _targets)
        {
            if (r != null) r.shadowCastingMode = mode;
        }
    }
}