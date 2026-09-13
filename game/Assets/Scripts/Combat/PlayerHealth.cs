// =====================================================================
//  PlayerHealth.cs
//  경로: game/Assets/Scripts/Combat/PlayerHealth.cs
//
//  체력, 사망, 리스폰. 모든 판정은 서버가 한다.
//  PlayerCharacter 프리팹에 붙인다.
//
//  NetworkVariable 로 상태를 노출해 클라이언트가 UI 를 그린다.
//  쓰기 권한은 서버에만 준다. 클라이언트가 값을 바꿀 수 없다.
//
//  ─────────────────────────────────────────────────────────────────
//  W8 Day 4 변경
//   Kills / Deaths 를 NetworkVariable 로 바꿨다.
//
//   기존에는 서버 전용 int 라 소유 클라이언트가 읽을 수 없었다.
//   session_summary 집계용으로만 쓰였고 화면에는 못 띄웠다.
//
//   쓰기 권한은 여전히 서버에만 있으므로 판정에는 영향이 없다.
//   클라이언트가 화면 숫자를 바꿔도 서버 값과 무관하다.
// =====================================================================

using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class PlayerHealth : NetworkBehaviour
{
    private readonly NetworkVariable<int> _health = new NetworkVariable<int>(
        WeaponConfig.MaxHealth,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> _dead = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // --- 통계 (session_summary + HUD) ---
    //
    // W8 Day 4: 서버 전용 int 에서 NetworkVariable 로 승격.
    // 소유 클라이언트가 우상단에 K/D 를 그린다.

    private readonly NetworkVariable<int> _kills = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int> _deaths = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public int Health => _health.Value;
    public bool IsDead => _dead.Value;

    public int Kills => _kills.Value;
    public int Deaths => _deaths.Value;

    /// <summary>사망/부활 시점에 클라이언트가 카메라를 전환하도록 알린다.</summary>
    public event System.Action<bool> OnDeadChanged;

    private PlayerController _controller;
    private PlayerRewind _rewind;

    public override void OnNetworkSpawn()
    {
        _controller = GetComponent<PlayerController>();
        _rewind = GetComponent<PlayerRewind>();

        _dead.OnValueChanged += HandleDeadChanged;
    }

    public override void OnNetworkDespawn()
    {
        _dead.OnValueChanged -= HandleDeadChanged;
    }

    private void HandleDeadChanged(bool _, bool now)
    {
        OnDeadChanged?.Invoke(now);
    }

    // -----------------------------------------------------------------
    //  데미지 (서버 전용)
    // -----------------------------------------------------------------

    /// <summary>
    /// 데미지를 적용한다. 서버에서만 호출된다.
    /// </summary>
    /// <returns>이 피격으로 사망했으면 true</returns>
    public bool ApplyDamage(int amount, PlayerHealth attacker)
    {
        if (!IsServer) return false;
        if (_dead.Value) return false;      // 이미 죽은 대상은 무시

        _health.Value = Mathf.Max(0, _health.Value - amount);
        if (_health.Value > 0) return false;

        _deaths.Value++;
        if (attacker != null && attacker != this) attacker._kills.Value++;

        _dead.Value = true;

        // 죽은 동안에는 히트박스를 빼서 시체 피격을 막는다.
        _rewind?.SetHitboxesActive(false);

        StartCoroutine(RespawnAfterDelay());
        return true;
    }

    private IEnumerator RespawnAfterDelay()
    {
        yield return new WaitForSeconds(WeaponConfig.RespawnDelaySec);
        if (!IsServer) yield break;

        Vector3 pos = SpawnManager.Instance != null
                    ? SpawnManager.Instance.GetNextSpawnPosition()
                    : new Vector3(0f, 1f, 0f);

        _health.Value = WeaponConfig.MaxHealth;
        _dead.Value = false;

        _rewind?.SetHitboxesActive(true);

        // ServerTeleport 안에서 V-MOVE 유예와 탄약 복구가 함께 처리된다.
        _controller?.ServerTeleport(pos);

        Debug.Log($"[RESPAWN] client={OwnerClientId} pos={pos} " +
                  $"K={_kills.Value} D={_deaths.Value}");
    }
}