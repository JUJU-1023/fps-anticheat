using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 3인칭 모델 애니메이션 구동. 소유자/원격 모두 동일 경로로 동작한다.
///
/// 이동 방향은 입력이 아니라 실제 Transform 이동량에서 뽑는다.
///   소유자       → PlayerController 예측이 움직인 결과
///   원격(클라)   → RemotePlayerInterpolator 가 보간한 결과
///   원격(서버)   → 서버 Simulate() 가 움직인 결과
///
/// 입력을 쓰면 서버가 거부한 입력에도 애니메이션이 반응해서,
/// 스피드핵 시연 때 다리는 뛰는데 위치는 안 나가는 그림이 된다.
///
/// 발사는 WeaponSystem.OnWeaponFired 를 구독한다. 서버가 승인한
/// 발사만 오므로 거부된 발사에 모션이 나오지 않는다.
///
/// 이 컴포넌트는 플레이어 루트에 붙인다. Animator 는 모델 자식에 있다.
/// </summary>
public class PlayerAnimationDriver : MonoBehaviour
{
    [Header("대상")]
    [Tooltip("모델 자식에 붙어 있는 Animator. 루트가 아니다.")]
    [SerializeField] private Animator animator;

    [Header("파라미터 이름")]
    [SerializeField] private string moveXParam = "MoveX";
    [SerializeField] private string moveYParam = "MoveY";
    [SerializeField] private string speedParam = "Speed";
    [SerializeField] private string fireTrigger = "Fire";

    [Header("상체 레이어")]
    [Tooltip("발사 애니메이션이 들어 있는 레이어 이름. 없으면 비워 둔다.")]
    [SerializeField] private string upperBodyLayerName = "UpperBody";

    [Tooltip("발사 후 상체 레이어 가중치가 0 으로 돌아가는 데 걸리는 시간(초).")]
    [SerializeField] private float upperBodyFadeSec = 0.25f;

    [Header("튜닝")]
    [Tooltip("블렌드 전환 부드러움(초). 값이 튀면 올린다.")]
    [SerializeField] private float dampTime = 0.10f;

    [Tooltip("한 프레임 이동량이 이 값을 넘으면 텔레포트로 보고 속도 0 (m).")]
    [SerializeField] private float teleportThreshold = 2.0f;

    [Header("진단 — Play 중 실측값이 여기 표시된다")]
    [SerializeField] private float debugSpeed;
    [SerializeField] private Vector2 debugMove;

    private int _moveXHash, _moveYHash, _speedHash, _fireHash;
    private int _upperBodyLayer = -1;
    private float _upperBodyWeight = 0f;

    private Vector3 _lastPos;
    private bool _hasLastPos;

    private WeaponSystem _weapon;

    public float CurrentSpeed => debugSpeed;

    private void Awake()
    {
        if (animator == null)
        {
            Debug.LogError($"[ANIMDRV] {name}: Animator 미지정. " +
                           "모델 자식의 Animator 를 인스펙터에 넣으세요.");
            enabled = false;
            return;
        }

        // 켜져 있으면 애니메이션이 Transform 을 직접 움직여
        // 서버 Simulate() 결과와 어긋나고 V-MOVE 오탐으로 이어진다.
        if (animator.applyRootMotion)
        {
            Debug.LogError($"[ANIMDRV] {name}: Apply Root Motion 이 켜져 있어 강제로 끕니다. " +
                           "Animator 컴포넌트에서도 체크를 해제하세요.");
            animator.applyRootMotion = false;
        }

        _moveXHash = Animator.StringToHash(moveXParam);
        _moveYHash = Animator.StringToHash(moveYParam);
        _speedHash = Animator.StringToHash(speedParam);
        _fireHash = Animator.StringToHash(fireTrigger);

        if (!string.IsNullOrEmpty(upperBodyLayerName))
        {
            _upperBodyLayer = animator.GetLayerIndex(upperBodyLayerName);
            if (_upperBodyLayer < 0)
                Debug.LogWarning($"[ANIMDRV] {name}: '{upperBodyLayerName}' 레이어 없음. " +
                                 "발사 애니메이션이 하체를 덮어씁니다.");
        }
    }

    private void OnEnable()
    {
        _hasLastPos = false;
        debugSpeed = 0f;
        debugMove = Vector2.zero;
        _upperBodyWeight = 0f;

        // WeaponSystem 은 NetworkBehaviour 라 OnNetworkSpawn 에서 이벤트를
        // 준비한다. 여기서 구독이 이를 수 있으므로 늦게 잡아도 되도록
        // LateUpdate 에서 재시도한다.
        TrySubscribe();
    }

    private void OnDisable()
    {
        if (_weapon != null)
        {
            _weapon.OnWeaponFired -= OnFired;
            _weapon = null;
        }
    }

    private void TrySubscribe()
    {
        if (_weapon != null) return;

        var ws = GetComponent<WeaponSystem>();
        if (ws == null) return;

        _weapon = ws;
        _weapon.OnWeaponFired += OnFired;
    }

    private void OnFired()
    {
        animator.SetTrigger(_fireHash);
        _upperBodyWeight = 1f;
    }

    // 보간기 Update() 이후여야 이번 프레임의 최종 위치가 잡힌다.
    private void LateUpdate()
    {
        TrySubscribe();
        UpdateUpperBodyWeight();

        Vector3 pos = transform.position;

        if (!_hasLastPos)
        {
            _lastPos = pos;
            _hasLastPos = true;
            return;
        }

        Vector3 delta = pos - _lastPos;
        delta.y = 0f;                 // 낙하/점프는 이동 속도에 넣지 않는다
        _lastPos = pos;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // 리스폰 ForceStateClientRpc, 보간기 OnTeleport 스냅.
        // 막지 않으면 부활할 때마다 전력질주 모션이 한 번 튄다.
        if (delta.sqrMagnitude > teleportThreshold * teleportThreshold)
        {
            debugSpeed = 0f;
            debugMove = Vector2.zero;
            animator.SetFloat(_speedHash, 0f);
            animator.SetFloat(_moveXHash, 0f);
            animator.SetFloat(_moveYHash, 0f);
            return;
        }

        Vector3 worldVel = delta / dt;
        debugSpeed = worldVel.magnitude;

        // 캐릭터 기준 방향으로 분해한다.
        // 루트에는 yaw 만 들어 있으므로(서버 Simulate, 보간기 모두)
        // InverseTransformDirection 결과의 x/z 가 곧 좌우/전후다.
        Vector3 localVel = transform.InverseTransformDirection(worldVel);
        debugMove = new Vector2(localVel.x, localVel.z);

        animator.SetFloat(_speedHash, debugSpeed, dampTime, dt);
        animator.SetFloat(_moveXHash, localVel.x, dampTime, dt);
        animator.SetFloat(_moveYHash, localVel.z, dampTime, dt);
    }

    private void UpdateUpperBodyWeight()
    {
        if (_upperBodyLayer < 0) return;

        if (_upperBodyWeight > 0f)
        {
            _upperBodyWeight -= Time.deltaTime / Mathf.Max(0.01f, upperBodyFadeSec);
            if (_upperBodyWeight < 0f) _upperBodyWeight = 0f;
        }

        animator.SetLayerWeight(_upperBodyLayer, _upperBodyWeight);
    }
}