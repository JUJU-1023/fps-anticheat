using UnityEngine;

/// <summary>
/// 1인칭 발사음. WeaponSystem.OnClientFired 를 구독한다.
///
/// 소유 클라이언트에서만 울린다. ClientTryFire 는 PlayerController.GatherInput
/// 안의 IsOwner 분기에서만 호출되기 때문이다.
///
/// 순수 클라이언트 표현이다. 서버 판정·되감기·텔레메트리와 무관하므로
/// W7 측정에 영향이 없다.
///
/// 배치
///   플레이어 루트(WeaponSystem 과 같은 GameObject)에 붙인다.
///   M16 오브젝트에 붙이면 사망 시 WeaponViewToggle 이 꺼서 소리가 끊긴다.
///
/// AudioSource 설정
///   Play On Awake  : 끔
///   Spatial Blend  : 0 (2D). 1인칭 총이라 거리 감쇠가 필요 없다.
///   Loop           : 끔
/// </summary>
[RequireComponent(typeof(WeaponSystem))]
public class WeaponAudio : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("비워두면 같은 GameObject 에서 찾고, 없으면 자동 생성한다.")]
    [SerializeField] private AudioSource source;

    [Header("Clips")]
    [Tooltip("발사음 변형들. 매 발 무작위로 하나 고른다. 하나만 넣어도 된다.")]
    [SerializeField] private AudioClip[] fireClips;

    // 직전에 쓴 클립을 기억해 연속으로 같은 소리가 나오는 걸 줄인다.
    private int lastClipIndex = -1;

    [Header("Mix")]
    [SerializeField, Range(0f, 1f)] private float volume = 0.65f;

    [Tooltip("발사마다 피치를 이 범위에서 흔든다. 연사가 기계음처럼 들리는 걸 막는다. " +
             "1,1 로 두면 변화 없음.")]
    [SerializeField] private Vector2 pitchRange = new Vector2(0.96f, 1.04f);

    private WeaponSystem weapon;

    private void Awake()
    {
        weapon = GetComponent<WeaponSystem>();

        if (source == null) source = GetComponent<AudioSource>();
        if (source == null)
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;   // 2D
            source.loop = false;
        }

        if (fireClips == null || fireClips.Length == 0)
            Debug.LogWarning($"[WeaponAudio] {name}: fireClips 가 비어 있습니다.");
    }

    private void OnEnable()
    {
        if (weapon != null) weapon.OnClientFired += HandleClientFired;
    }

    private void OnDisable()
    {
        if (weapon != null) weapon.OnClientFired -= HandleClientFired;
    }

    /// <param name="shotIndex">이번 발의 연사 인덱스. 0 이 연사 첫 발.</param>
    private void HandleClientFired(int shotIndex)
    {
        if (source == null || fireClips == null || fireClips.Length == 0) return;

        AudioClip clip = PickClip();
        if (clip == null) return;

        source.pitch = Random.Range(pitchRange.x, pitchRange.y);
        source.PlayOneShot(clip, volume);
    }

    /// <summary>
    /// 직전과 다른 클립을 고른다. 순수 랜덤이면 같은 소리가 두세 번
    /// 연달아 나와 오히려 기계적으로 들린다.
    /// </summary>
    private AudioClip PickClip()
    {
        if (fireClips.Length == 1) return fireClips[0];

        int i;
        do { i = Random.Range(0, fireClips.Length); }
        while (i == lastClipIndex);

        lastClipIndex = i;
        return fireClips[i];
    }
}
