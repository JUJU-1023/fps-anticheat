// =====================================================================
//  WeaponKick.cs
//  경로: game/Assets/Scripts/Combat/WeaponKick.cs
//
//  1인칭 총기 킥. 발사 순간 총 모델을 뒤/위로 밀고 스프링처럼 되돌린다.
//
//  ★ 카메라와 currentPitch 는 건드리지 않는다. ★
//    시야 반동은 이미 PlayerController.GatherInput 에서
//    WeaponConfig.GetRecoil 로 currentPitch 에 적용되고,
//    그 값이 InputPayload.pitch 로 서버에 전달되어 검증된다.
//    여기서 카메라를 흔들면 V-RECOIL-01 의 판정 표본이 오염된다.
//
//    이 스크립트는 총 모델의 localPosition / localRotation 만 만진다.
//    순수 클라이언트 표현이라 서버 판정과 무관하다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 회전 중심 (pivotOffset) ★
//
//  Transform 회전은 모델의 원점을 축으로 돈다. M16 프리팹의 원점이
//  총구 쪽에 있으면 그 점이 고정되고 개머리판이 들린다. 실제 총은
//  어깨와 손목이 고정되고 총구가 올라가므로 반대다.
//
//  pivotOffset 은 기준 위치에서 회전축까지의 오프셋이다.
//  (0, 0, -0.30) 이면 30cm 뒤, 즉 사수 어깨 쪽이 축이 되어 총구가
//  위로 솟는다. 계층을 바꾸지 않고 조정할 수 있다.
//
//  감각
//    z 를 더 음수로  → 축이 뒤로. 총구가 더 크게 솟는다
//    z = 0           → 모델 원점 기준 (기존 동작)
//    z 가 양수       → 축이 앞으로. 개머리판이 들린다
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 회전을 부모(카메라) 공간에서 적용한다 ★
//
//  기존에는 baseRot * q 였다. 이러면 모델 자신의 로컬 축으로 돌기
//  때문에, 모델이 +Z 가 아닌 방향으로 제작됐으면 엉뚱한 축이 회전한다.
//
//  q * baseRot 으로 바꾸면 카메라 기준 축으로 돈다. 모델이 어떻게
//  제작됐든 "화면에서 총구가 위로" 가 보장된다.
//  baseRot 이 항등이면 둘은 같으므로 기존 설정에는 영향이 없다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ 킥 값에 음수를 넣을 수 있다 ★
//
//  방향이 반대로 보이면 kickPitch 에 음수를 넣으면 된다.
//  누적 상한을 Abs 로 계산하므로 부호와 무관하게 동작한다.
//
//  ─────────────────────────────────────────────────────────────────
//  배치 가이드
//
//     PlayerCharacter (root)   ← WeaponSystem, PlayerController, WeaponKick
//       └ Camera               localPosition = WeaponConfig.EyeOffset
//           └ M16              localPosition ≈ (0.22, -0.18, 0.45)
//
//   카메라 기준 좌표의 감각
//     x  오른손잡이면 +0.15 ~ +0.30
//     y  화면 아래쪽이라 -0.12 ~ -0.25
//     z  앞쪽. 0.3 ~ 0.6. 이보다 크면 총이 멀리 떠 보인다.
//
//   거리가 멀수록 같은 킥도 화면상 변화가 작아진다. 3.5m 에 놓인
//   총은 4.5cm 킥이 1픽셀 수준이라 사실상 안 보인다.
//
//  ─────────────────────────────────────────────────────────────────
//  진단
//
//   [RequireComponent] 를 쓰지 않는다. 이 스크립트를 M16 이나 Camera 에
//   붙이면 Unity 가 그 오브젝트에 WeaponSystem 을 자동 생성하는데,
//   그 가짜의 OnClientFired 는 영원히 울리지 않기 때문이다.
//   대신 PlayerController 가 있는 루트에서 WeaponSystem 을 가져온다.
//
//   컴포넌트 우클릭 → "테스트 킥" 으로 발사 이벤트를 거치지 않고
//   킥을 직접 발동할 수 있다. 원인을 반으로 가르는 도구다.
// =====================================================================

using UnityEngine;

public class WeaponKick : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("움직일 총 모델. 카메라의 자식이어야 한다.")]
    [SerializeField] private Transform weaponModel;

    [Header("Diagnostics")]
    [Tooltip("배선 상태와 적용 결과를 로그로 남긴다. 확인이 끝나면 끈다.")]
    [SerializeField] private bool verboseDiagnostics = true;

    [Header("Rest pose")]
    [Tooltip("체크하면 아래 값을 기준 자세로 쓴다. " +
             "끄면 Awake 시점의 Transform 을 그대로 기준으로 삼는다.")]
    [SerializeField] private bool useExplicitRestPose = false;

    [Tooltip("카메라 기준 총 모델 위치. 오른손잡이 기준 대략 (0.22, -0.18, 0.45).")]
    [SerializeField] private Vector3 restPosition = new Vector3(0.22f, -0.18f, 0.45f);

    [Tooltip("카메라 기준 총 모델 회전(도).")]
    [SerializeField] private Vector3 restEuler = Vector3.zero;

    [Tooltip("기준 위치가 이 거리를 넘으면 배치 오류로 보고 경고한다(m).")]
    [SerializeField] private float sanityDistance = 1.5f;

    [Header("Pivot")]
    [Tooltip("기준 위치에서 회전축까지의 오프셋(카메라 기준, m).\n" +
             "z 를 음수로 두면 축이 뒤(어깨 쪽)로 가서 총구가 올라간다.\n" +
             "(0,0,0) 이면 모델 원점을 축으로 돌아 개머리판이 들릴 수 있다.")]
    [SerializeField] private Vector3 pivotOffset = new Vector3(0f, 0f, -0.30f);

    [Header("Kick per shot")]
    [Tooltip("발사마다 뒤로 밀리는 거리(m). 음수 Z 방향.")]
    [SerializeField] private float kickBack = 0.045f;

    [Tooltip("발사마다 들리는 거리(m).")]
    [SerializeField] private float kickUp = 0.012f;

    [Tooltip("발사마다 총구가 위로 들리는 각도(도). 방향이 반대면 음수를 넣는다.")]
    [SerializeField] private float kickPitch = 1.5f;

    [Tooltip("발사마다 좌우로 흔들리는 각도(도). 매 발 부호가 랜덤. " +
             "클라이언트 표현이므로 서버 판정과 무관하다.")]
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
    private PlayerHealth health;

    // 총 모델의 기준 자세.
    private Vector3 basePos;
    private Quaternion baseRot;

    // 목표 오프셋(발사 시 튀어오름) / 현재 오프셋(목표를 따라감)
    private Vector3 targetPos;
    private Vector3 currentPos;
    private Vector3 targetEuler;
    private Vector3 currentEuler;

    // --- 진단 상태 ---
    private Vector3 _lastAppliedPos;
    private bool _hasApplied;
    private bool _overrideWarned;
    private int _fireCount;

    private void Awake()
    {
        ResolveWeapon();

        if (weaponModel == null)
        {
            Debug.LogError(
                $"[WeaponKick] '{name}': weaponModel 이 비어 있습니다. " +
                "Inspector 에서 총 모델을 지정하세요. 킥을 비활성화합니다.");
            enabled = false;
            return;
        }

        ResolveRestPose();
        LogWiring();
    }

    /// <summary>
    /// 발사 이벤트를 줄 WeaponSystem 을 찾는다.
    ///
    /// GetComponent 가 아니라 PlayerController 가 있는 루트에서 가져온다.
    /// 이 스크립트가 M16 이나 Camera 에 붙어 있어도 동작하게 하기 위해서다.
    /// </summary>
    private void ResolveWeapon()
    {
        var root = GetComponentInParent<PlayerController>();

        if (root != null)
        {
            weapon = root.GetComponent<WeaponSystem>();
            health = root.GetComponent<PlayerHealth>();

            if (root.gameObject != gameObject)
            {
                Debug.LogWarning(
                    $"[WeaponKick] '{name}' 에 붙어 있습니다. " +
                    $"플레이어 루트 '{root.name}' 에 붙이는 것을 권장합니다. " +
                    "동작에는 문제없습니다.");

                // 잘못 붙인 오브젝트에 WeaponSystem 이 자동 생성돼 있으면
                // 발사 판정이 꼬인다. 예전 [RequireComponent] 의 잔재다.
                if (GetComponent<WeaponSystem>() != null)
                {
                    Debug.LogError(
                        $"[WeaponKick] '{name}' 에 불필요한 WeaponSystem 이 붙어 있습니다. " +
                        "Inspector 에서 삭제하세요.");
                }
            }
        }
        else
        {
            // PlayerController 를 못 찾는 구성. 차선책으로 부모를 훑는다.
            weapon = GetComponentInParent<WeaponSystem>();
            health = GetComponentInParent<PlayerHealth>();
        }

        if (weapon == null)
        {
            Debug.LogError(
                $"[WeaponKick] '{name}': WeaponSystem 을 찾지 못했습니다. " +
                "플레이어 프리팹 안에 있는지 확인하세요. 킥을 비활성화합니다.");
            enabled = false;
        }
    }

    /// <summary>
    /// 기준 자세를 정하고, 값이 비상식적이면 경고한다.
    ///
    /// 1인칭 뷰모델은 카메라에서 1m 안쪽에 있어야 한다. 그보다 멀면
    /// 총이 공중에 떠 보이고, 같은 킥이라도 화면상 변화가 작아져
    /// 움직이지 않는 것처럼 보인다.
    /// </summary>
    private void ResolveRestPose()
    {
        if (useExplicitRestPose)
        {
            basePos = restPosition;
            baseRot = Quaternion.Euler(restEuler);
            weaponModel.localPosition = basePos;
            weaponModel.localRotation = baseRot;
            return;
        }

        basePos = weaponModel.localPosition;
        baseRot = weaponModel.localRotation;

        if (basePos.magnitude > sanityDistance)
        {
            Debug.LogWarning(
                $"[WeaponKick] 총 모델이 부모에서 {basePos.magnitude:F2}m 떨어져 있습니다 " +
                $"(localPosition={basePos}). 1인칭 뷰모델로는 비정상입니다. " +
                "이 거리에서는 수 cm 킥이 화면에서 거의 안 보입니다. " +
                "Use Explicit Rest Pose 를 켜고 (0.22, -0.18, 0.45) 부근으로 맞추세요.");
        }
    }

    /// <summary>배선 상태를 한 번 남긴다. 킥이 안 보일 때 가장 먼저 볼 줄이다.</summary>
    private void LogWiring()
    {
        if (!verboseDiagnostics) return;

        string parentName = weaponModel.parent != null ? weaponModel.parent.name : "(없음)";
        var anim = weaponModel.GetComponentInParent<Animator>();

        Debug.Log(
            $"[WeaponKick] 배선 확인\n" +
            $"  스크립트 위치 : {name}\n" +
            $"  WeaponSystem  : {(weapon != null ? weapon.gameObject.name : "NULL")}\n" +
            $"  총 모델       : {weaponModel.name} (부모 {parentName})\n" +
            $"  기준 위치     : {basePos} (거리 {basePos.magnitude:F2}m)\n" +
            $"  회전축 오프셋 : {pivotOffset}\n" +
            $"  킥 크기       : back={kickBack} up={kickUp} pitch={kickPitch}");

        if (anim != null)
        {
            Debug.LogWarning(
                $"[WeaponKick] '{anim.gameObject.name}' 에 Animator 가 있습니다. " +
                "애니메이션이 총 모델의 Transform 을 매 프레임 덮어쓰면 킥이 보이지 않습니다.");
        }
    }

    private void OnEnable()
    {
        if (weapon != null) weapon.OnClientFired += HandleClientFired;
        if (health != null) health.OnDeadChanged += HandleDeadChanged;
    }

    private void OnDisable()
    {
        if (weapon != null) weapon.OnClientFired -= HandleClientFired;
        if (health != null) health.OnDeadChanged -= HandleDeadChanged;

        // 비활성화될 때 기준 자세로 되돌린다.
        if (weaponModel != null)
        {
            weaponModel.localPosition = basePos;
            weaponModel.localRotation = baseRot;
        }
        targetPos = currentPos = Vector3.zero;
        targetEuler = currentEuler = Vector3.zero;
        _hasApplied = false;
    }

    /// <summary>
    /// 사망하면 총 모델을 숨긴다.
    ///
    /// CombatHUD.HandleDead 가 카메라를 SetParent(null) 로 떼어 부감으로
    /// 보내는데, 총 모델이 카메라의 자식이라 같이 딸려간다.
    /// 숨기지 않으면 사망 화면에 총이 떠다닌다.
    /// </summary>
    private void HandleDeadChanged(bool dead)
    {
        if (weaponModel == null) return;

        if (dead)
        {
            // 부활 시 킥이 남아 있지 않도록 정리한다.
            targetPos = currentPos = Vector3.zero;
            targetEuler = currentEuler = Vector3.zero;
            weaponModel.localPosition = basePos;
            weaponModel.localRotation = baseRot;
            _hasApplied = false;
        }

        weaponModel.gameObject.SetActive(!dead);
    }

    /// <param name="shotIndex">이번 발의 연사 인덱스. 0 이 연사 첫 발.</param>
    private void HandleClientFired(int shotIndex)
    {
        _fireCount++;

        // 처음 몇 발만 남긴다. 매 발 찍으면 로그가 넘친다.
        if (verboseDiagnostics && _fireCount <= 3)
            Debug.Log($"[WeaponKick] 발사 수신 shot={shotIndex} ({_fireCount}번째)");

        // 연사할수록 조금씩 더 크게. 다만 maxStack 에서 멈춘다.
        // 상한이 없으면 30발 연사에 총이 화면 밖으로 나간다.
        //
        // 상한을 Abs 로 잡으므로 킥 값에 음수를 넣어도 된다.
        // 방향이 반대로 보이면 인스펙터에서 부호만 바꾸면 된다.
        float limBack = Mathf.Abs(kickBack) * maxStack;
        float limUp = Mathf.Abs(kickUp) * maxStack;
        float limPitch = Mathf.Abs(kickPitch) * maxStack;
        float limYaw = Mathf.Abs(kickYaw) * maxStack;

        targetPos.z = Mathf.Clamp(targetPos.z - kickBack, -limBack, limBack);
        targetPos.y = Mathf.Clamp(targetPos.y + kickUp, -limUp, limUp);

        targetEuler.x = Mathf.Clamp(targetEuler.x - kickPitch, -limPitch, limPitch);
        targetEuler.y = Mathf.Clamp(targetEuler.y + Random.Range(-kickYaw, kickYaw),
                                    -limYaw, limYaw);
    }

    private void LateUpdate()
    {
        if (weaponModel == null) return;

        DetectOverride();

        float dt = Time.deltaTime;

        // 1) 목표 오프셋이 0 으로 감쇠 — 스프링의 복원력
        targetPos = Vector3.Lerp(targetPos, Vector3.zero, 1f - Mathf.Exp(-returnSpeed * dt));
        targetEuler = Vector3.Lerp(targetEuler, Vector3.zero, 1f - Mathf.Exp(-returnSpeed * dt));

        // 2) 현재 오프셋이 목표를 따라감 — 관성
        //    Exp 형태라 프레임률이 달라도 같은 속도로 수렴한다.
        currentPos = Vector3.Lerp(currentPos, targetPos, 1f - Mathf.Exp(-snappiness * dt));
        currentEuler = Vector3.Lerp(currentEuler, targetEuler, 1f - Mathf.Exp(-snappiness * dt));

        ApplyPose();
    }

    /// <summary>
    /// 기준 자세 + 현재 킥을 총 모델에 적용한다.
    ///
    /// 회전은 부모(카메라) 공간에서 건다. q * baseRot 이므로 모델이
    /// 어떤 방향으로 제작됐든 화면 기준 축으로 돈다.
    ///
    /// 회전축은 basePos + pivotOffset 이다. 모델을 그 점 둘레로 돌린
    /// 뒤 원점이 가야 할 위치를 역산한다.
    ///
    ///   회전 후 원점 = P + q * (basePos - P)
    ///               = basePos + (pivotOffset - q * pivotOffset)
    ///
    /// pivotOffset 이 0 이면 기존 동작(모델 원점 기준)과 같다.
    /// </summary>
    private void ApplyPose()
    {
        Quaternion q = Quaternion.Euler(currentEuler);
        Vector3 pivotComp = pivotOffset - q * pivotOffset;

        weaponModel.localPosition = basePos + pivotComp + currentPos;
        weaponModel.localRotation = q * baseRot;

        _lastAppliedPos = weaponModel.localPosition;
        _hasApplied = true;
    }

    /// <summary>
    /// 지난 프레임에 적용한 값이 그대로 남아 있는지 본다.
    ///
    /// 달라졌다면 Animator 나 다른 스크립트가 총 모델의 Transform 을
    /// 덮어쓰고 있다는 뜻이다. 킥 계산은 정상인데 화면에는 안 보이는
    /// 상태이므로, 원인을 모르면 한참 헤맨다. 한 번만 경고한다.
    /// </summary>
    private void DetectOverride()
    {
        if (!_hasApplied || _overrideWarned || !verboseDiagnostics) return;

        if ((weaponModel.localPosition - _lastAppliedPos).sqrMagnitude < 1e-8f) return;

        _overrideWarned = true;
        Debug.LogWarning(
            $"[WeaponKick] 총 모델의 Transform 이 외부에서 덮어써지고 있습니다.\n" +
            $"  적용한 값 : {_lastAppliedPos}\n" +
            $"  현재 값   : {weaponModel.localPosition}\n" +
            "  Animator, 다른 스크립트, 또는 부모의 Constraint 를 확인하세요.");
    }

#if UNITY_EDITOR
    /// <summary>
    /// 발사 이벤트를 거치지 않고 킥을 직접 발동한다.
    ///
    ///   총이 움직이면   → Transform 적용은 정상. 발사 이벤트가 안 온다.
    ///   안 움직이면     → Transform 적용이 막혀 있다.
    /// </summary>
    [ContextMenu("테스트 킥 (Play 중에만)")]
    private void TestKick()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[WeaponKick] Play 중에만 동작합니다.");
            return;
        }
        if (weaponModel == null)
        {
            Debug.LogWarning("[WeaponKick] weaponModel 이 없습니다.");
            return;
        }

        HandleClientFired(0);
        Debug.Log("[WeaponKick] 테스트 킥 발동.");
    }

    /// <summary>
    /// 현재 총 모델의 Transform 을 restPosition / restEuler 에 복사한다.
    /// 씬 뷰에서 위치를 눈으로 맞춘 뒤 쓰면 값이 들어온다.
    /// </summary>
    [ContextMenu("현재 자세를 기준 자세로 복사")]
    private void CaptureRestPose()
    {
        if (weaponModel == null) return;

        restPosition = weaponModel.localPosition;
        restEuler = weaponModel.localRotation.eulerAngles;
        useExplicitRestPose = true;

        Debug.Log($"[WeaponKick] 기준 자세 저장: pos={restPosition} euler={restEuler}");
        UnityEditor.EditorUtility.SetDirty(this);
    }
#endif
}