using UnityEngine;

/// <summary>
/// 사망 중 1인칭 총기 모델을 숨긴다.
///
/// 총기가 카메라의 자식이라 사망 카메라가 올라갈 때 같이 딸려 올라간다.
/// 시야에 총이 떠다니는 것처럼 보이므로 사망 동안만 꺼둔다.
///
/// 순수 클라이언트 비주얼이다. 서버 판정·히트박스·텔레메트리와 무관하므로
/// W7 측정에 영향이 없다. (총기 오브젝트에는 콜라이더가 없어야 한다)
///
/// 배치
///   플레이어 루트(PlayerHealth 와 같은 GameObject)에 붙이고,
///   weaponView 에 M16 오브젝트를 드래그한다.
/// </summary>
[RequireComponent(typeof(PlayerHealth))]
public class WeaponViewToggle : MonoBehaviour
{
    [Tooltip("사망 중 숨길 1인칭 총기 모델. 보통 Camera 의 자식인 M16.")]
    [SerializeField] private GameObject weaponView;

    private PlayerHealth health;

    // 상태가 바뀔 때만 SetActive 를 부른다. 매 프레임 호출하면
    // OnEnable/OnDisable 이 계속 돌아 애니메이터나 파티클이 리셋된다.
    private bool lastDead;
    private bool applied;

    private void Awake()
    {
        health = GetComponent<PlayerHealth>();

        if (weaponView == null)
            Debug.LogWarning($"[WeaponViewToggle] {name}: weaponView 가 비어 있습니다. " +
                             $"Inspector 에서 M16 을 지정하세요.");
    }

    private void LateUpdate()
    {
        if (health == null || weaponView == null) return;

        bool dead = health.IsDead;
        if (applied && dead == lastDead) return;

        lastDead = dead;
        applied  = true;
        weaponView.SetActive(!dead);
    }
}
