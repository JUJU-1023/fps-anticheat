using UnityEngine;

/// <summary>
/// 1인칭 총기 킥. 발사 순간 총 모델을 뒤/위로 밀고 스프링처럼 되돌린다.
///
/// ★ 카메라와 currentPitch 는 건드리지 않는다. ★
///   시야 반동은 이미 PlayerController.GatherInput 에서
///   WeaponConfig.GetRecoil 로 currentPitch 에 적용되고,
///   그 값이 InputPayload.pitch 로 서버에 전달되어 검증된다.
///   여기서 카메라를 흔들면 W8 노리코일 탐지의 기준선이 오염된다.
///
///   이 스크립트는 총 모델의 localPosition / localRotation 만 만진다.
///   순수 클라이언트 표현이라 W7 측정에 영향이 없다.
///
/// 배치
///   플레이어 루트(WeaponSystem 과 같은 GameObject)에 붙이고,
///   weaponModel 에 M16 오브젝트를 드래그한다.
///
///   M16 의 Transform 은 이 스크립트가 매 프레임 덮어쓴다.
///   위치를 조정하고 싶으면 M16 을 빈 부모 안에 넣고 그 부모를 옮기거나,
///   Play 전 상태를 기준으로 잡히므로 Play 를 끄고 조정하면 된다.
/// </summary>
[RequireComponent(typeof(WeaponSystem))]
public class WeaponKick : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("움직일 총 모델. 보통 Camera 의 자식인 M16.")]
    [SerializeField] private Transform weaponModel;

    [Header("Kick per shot")]
    [Tooltip("발사마다 뒤로 밀리는 거리(m). 음수 Z 방향.")]
    [SerializeField] private float kickBack = 0.045f;

    [Tooltip("발사마다 들리는 거리(m).")]
    [SerializeField] private float kickUp = 0.012f;

    [Tooltip("발사마다 총구가 위로 들리는 각도(도).")]
    [SerializeField] private float kickPitch = 3.2f;

    [Tooltip("발사마다 좌우로 흔들리는 각도(도). 매 발 부호가 랜덤.")]
    [SerializeField] private float kickYaw = 0.9f;

    [Header("Accumulation")]
    [Tooltip("연사 중 킥이 누적될 수 있는 최대 배수. 1이면 누적 없음.")]
    [SerializeField, Range(1f, 5f)] private float maxStack = 2.5f;

    [Header("Recovery")]
    [Tooltip("클수록 빨리 원위치로 돌아온다.")]
    [SerializeField] private float returnSpeed = 14f;

    [Tooltip("클수록 킥이 즉각적이다. 낮추면 물렁해진다.")]
    [SerializeField] private float snappiness = 26f;

    private WeaponSystem weapon;

    // 총 모델의 기준 자세. Awake 시점 값을 원점으로 삼는다.
    private Vector3 basePos;
    private Quaternion baseRot;

    // 목표 오프셋(발사 시 튀어오름) / 현재 오프셋(목표를 따라감)
    private Vector3 targetPos;
    private Vector3 currentPos;
    private Vector3 targetEuler;
    private Vector3 currentEuler;

    private void Awake()
    {
        weapon = GetComponent<WeaponSystem>();

        if (weaponModel == null)
        {
            Debug.LogWarning($"[WeaponKick] {name}: weaponModel 이 비어 있습니다. " +
                             $"Inspector 에서 M16 을 지정하세요.");
            enabled = false;
            return;
        }

        basePos = weaponModel.localPosition;
        baseRot = weaponModel.localRotation;
    }

    private void OnEnable()
    {
        if (weapon != null) weapon.OnClientFired += HandleClientFired;
    }

    private void OnDisable()
    {
        if (weapon != null) weapon.OnClientFired -= HandleClientFired;

        // 비활성화될 때(사망 등) 기준 자세로 되돌린다.
        if (weaponModel != null)
        {
            weaponModel.localPosition = basePos;
            weaponModel.localRotation = baseRot;
        }
        targetPos = currentPos = Vector3.zero;
        targetEuler = currentEuler = Vector3.zero;
    }

    /// <param name="shotIndex">이번 발의 연사 인덱스. 0 이 연사 첫 발.</param>
    private void HandleClientFired(int shotIndex)
    {
        Debug.Log($"[KICK] shot={shotIndex}");
        // 연사할수록 조금씩 더 크게. 다만 maxStack 에서 멈춘다.
        // 상한이 없으면 30발 연사에 총이 화면 밖으로 나간다.
        float stackLimitBack  = kickBack  * maxStack;
        float stackLimitUp    = kickUp    * maxStack;
        float stackLimitPitch = kickPitch * maxStack;

        targetPos.z = Mathf.Max(targetPos.z - kickBack, -stackLimitBack);
        targetPos.y = Mathf.Min(targetPos.y + kickUp,    stackLimitUp);

        targetEuler.x = Mathf.Max(targetEuler.x - kickPitch, -stackLimitPitch);
        targetEuler.y += Random.Range(-kickYaw, kickYaw);
        targetEuler.y  = Mathf.Clamp(targetEuler.y, -kickYaw * maxStack, kickYaw * maxStack);
    }

    private void LateUpdate()
    {
        if (weaponModel == null) return;

        float dt = Time.deltaTime;

        // 1) 목표 오프셋이 0 으로 감쇠 — 스프링의 복원력
        targetPos   = Vector3.Lerp(targetPos,   Vector3.zero, 1f - Mathf.Exp(-returnSpeed * dt));
        targetEuler = Vector3.Lerp(targetEuler, Vector3.zero, 1f - Mathf.Exp(-returnSpeed * dt));

        // 2) 현재 오프셋이 목표를 따라감 — 관성
        //    Exp 형태라 프레임률이 달라도 같은 속도로 수렴한다.
        currentPos   = Vector3.Lerp(currentPos,   targetPos,   1f - Mathf.Exp(-snappiness * dt));
        currentEuler = Vector3.Lerp(currentEuler, targetEuler, 1f - Mathf.Exp(-snappiness * dt));

        weaponModel.localPosition = basePos + currentPos;
        weaponModel.localRotation = baseRot * Quaternion.Euler(currentEuler);

        if (currentPos.sqrMagnitude > 0.0001f)
            Debug.Log($"[KICK] applied pos={currentPos} euler={currentEuler} " +
                      $"actual={weaponModel.localPosition}");
    }
}
