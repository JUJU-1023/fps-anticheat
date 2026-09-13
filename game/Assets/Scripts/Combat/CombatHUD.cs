// =====================================================================
//  CombatHUD.cs
//  경로: game/Assets/Scripts/Combat/CombatHUD.cs
//
//  소유 클라이언트 전용 전투 UI. PlayerCharacter 프리팹에 붙인다.
//
//  포함
//   - 체력바 (좌하단, 잔량에 따라 색 변화)
//   - 탄약 표시 (우하단, 탄창 눈금 + 여분 바 + 재장전 진행)  ← W8 Day 4
//   - K/D (우상단)                                          ← W8 Day 4
//   - 히트마커 (중앙 X, 명중 시 잠깐 표시 / 헤드샷은 노랑 / 킬은 빨강)
//   - 사망 오버레이 + 3인칭 부감 카메라
//
//  UI 를 코드로 생성하는 이유
//   프리팹/씬 편집 단계를 줄이기 위해서다. W8.5 빌드 프리즈를 앞두고
//   씬 변경은 리스크가 크다.
//
//  ─────────────────────────────────────────────────────────────────
//  ★ W8 Day 4 : 탄약을 왜 눈금으로 그리는가 ★
//
//   이 파일은 폰트 리소스에 의존하지 않는다는 원칙으로 작성됐다.
//   숫자를 쓰려면 내장 폰트가 필요한데, Unity 버전에 따라 이름이
//   Arial.ttf 에서 LegacyRuntime.ttf 로 바뀌었고 없을 수도 있다.
//
//   그래서 탄창을 MagSize 개의 눈금으로 그린다. 폰트가 없어도 되고,
//   데모 영상에서 한 발씩 줄어드는 것이 숫자보다 잘 보인다.
//   내장 폰트가 잡히면 "30 / 150" 텍스트와 K/D 를 얹는다.
//
//   ※ K/D 는 눈금으로 대체할 수 없으므로 폰트가 없으면 표시되지 않는다.
//     그 경우 서버 로그의 [RESPAWN] 줄에 K/D 가 남는다.
//
//   서버 권위 값을 그대로 읽는다. WeaponSystem 의 Ammo / Reserve /
//   IsReloading 과 PlayerHealth 의 Kills / Deaths 는 모두
//   NetworkVariable 이고 쓰기 권한이 서버에만 있으므로,
//   클라이언트가 화면 숫자를 바꿔도 판정에는 영향이 없다.
//
//   ※ 클라이언트 예측(_clientAmmo)이 아니라 서버 값을 그린다.
//     둘이 어긋나는 순간이 있지만(RTT 한 번), 화면이 서버와 다른 것보다
//     한 틱 늦는 편이 낫다.
// =====================================================================

using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

public class CombatHUD : NetworkBehaviour
{
    [Tooltip("이미 만들어 둔 크로스헤어가 있으면 연결한다. 사망 시 자동으로 숨긴다.")]
    [SerializeField] private GameObject existingCrosshair;

    [Tooltip("스프라이트를 직접 넣으면 화면 중앙에 그려준다.")]
    [SerializeField] private Sprite crosshairSprite;
    [SerializeField] private float crosshairSize = 32f;

    private GameObject _crosshairGo;

    [Header("사망 카메라")]
    [SerializeField] private Vector3 deathCamOffset = new Vector3(0f, 8f, -6f);

    private PlayerHealth _health;
    private WeaponSystem _weapon;
    private Camera _cam;

    private RectTransform _healthFill;
    private Image _healthFillImg;
    private float _healthBarWidth = 240f;

    // --- 탄약 (W8 Day 4) ---
    private const float AmmoStripWidth = 240f;
    private const float AmmoStripHeight = 18f;
    private const float AmmoSegGap = 2f;

    private Image[] _magSegments;
    private RectTransform _reserveFill;
    private Image _reserveFillImg;
    private RectTransform _reloadFill;
    private GameObject _reloadBarGo;
    private Text _ammoText;

    private static readonly Color SegLoaded = new Color(0.92f, 0.92f, 0.90f, 0.95f);
    private static readonly Color SegEmpty = new Color(1f, 1f, 1f, 0.13f);
    private static readonly Color SegLow = new Color(0.95f, 0.45f, 0.25f, 0.95f);
    private static readonly Color ReloadCol = new Color(0.95f, 0.75f, 0.20f, 0.9f);

    /// <summary>재장전이 시작된 시각. 진행 막대를 그리는 데 쓴다.</summary>
    private float _reloadStartedAt = -1f;
    private bool _wasReloading;

    // --- K/D (W8 Day 4) ---
    private Text _scoreText;
    private int _lastKills = -1;
    private int _lastDeaths = -1;

    private GameObject _hitmarker;
    private Image[] _hitmarkerParts;
    private Coroutine _hitmarkerCo;

    private Image _deathOverlay;

    private bool _deathCamActive;

    // -----------------------------------------------------------------
    //  수명 주기
    // -----------------------------------------------------------------

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) { enabled = false; return; }

        _health = GetComponent<PlayerHealth>();
        _weapon = GetComponent<WeaponSystem>();
        _cam = GetComponentInChildren<Camera>(true);

        BuildUI();

        if (_weapon != null) _weapon.OnHitConfirmed += HandleHit;
        if (_health != null) _health.OnDeadChanged += HandleDead;
    }

    public override void OnNetworkDespawn()
    {
        if (_weapon != null) _weapon.OnHitConfirmed -= HandleHit;
        if (_health != null) _health.OnDeadChanged -= HandleDead;
    }

    private void Update()
    {
        if (!IsOwner || _health == null) return;

        // NetworkVariable 변경 콜백을 따로 두지 않고 매 프레임 반영한다.
        // 값 하나짜리라 비용이 무의미하다.
        float ratio = Mathf.Clamp01((float)_health.Health / WeaponConfig.MaxHealth);
        if (_healthFill != null)
        {
            _healthFill.sizeDelta = new Vector2(_healthBarWidth * ratio, 18f);
            _healthFillImg.color = ratio > 0.5f ? new Color(0.30f, 0.85f, 0.35f)
                                  : ratio > 0.25f ? new Color(0.95f, 0.75f, 0.20f)
                                                  : new Color(0.90f, 0.25f, 0.25f);
        }

        UpdateAmmo();
        UpdateScore();

        if (_deathCamActive && _cam != null)
        {
            // 시체를 계속 내려다본다.
            Vector3 target = transform.position;
            _cam.transform.position = target + deathCamOffset;
            _cam.transform.LookAt(target + Vector3.up * 0.5f);
        }
    }

    // -----------------------------------------------------------------
    //  K/D (W8 Day 4)
    // -----------------------------------------------------------------

    /// <summary>
    /// 서버 권위 K/D 를 반영한다.
    ///
    /// 값이 바뀔 때만 문자열을 만든다. Text.text 에 대입하면 항상
    /// 메시 재생성이 일어나므로 매 프레임 쓰면 낭비다.
    ///
    /// 2인 데모에서는 상대의 K/D 가 곧 내 D/K 라서 내 것만으로 충분하다.
    /// 전체 점수판이 필요해지면 NetworkManager.ConnectedClientsList 를
    /// 순회해 각자의 PlayerHealth 를 읽으면 된다.
    /// </summary>
    private void UpdateScore()
    {
        if (_scoreText == null || _health == null) return;

        int k = _health.Kills;
        int d = _health.Deaths;
        if (k == _lastKills && d == _lastDeaths) return;

        _lastKills = k;
        _lastDeaths = d;
        _scoreText.text = $"K {k}   D {d}";
    }

    // -----------------------------------------------------------------
    //  탄약 (W8 Day 4)
    // -----------------------------------------------------------------

    /// <summary>
    /// 서버 권위 탄약을 화면에 반영한다.
    ///
    /// 재장전 진행은 IsReloading 이 false→true 로 바뀐 시각을 잡아
    /// 로컬에서 보간한다. 서버가 종료 시각을 따로 보내지 않으므로
    /// 화면 표시 목적으로만 쓴다. 판정과는 무관하다.
    /// </summary>
    private void UpdateAmmo()
    {
        if (_weapon == null || _magSegments == null) return;

        int ammo = Mathf.Clamp(_weapon.Ammo, 0, WeaponConfig.MagSize);
        bool reloading = _weapon.IsReloading;

        // --- 재장전 시작 시각 ---
        if (reloading && !_wasReloading)
            _reloadStartedAt = Time.unscaledTime;
        else if (!reloading)
            _reloadStartedAt = -1f;
        _wasReloading = reloading;

        // --- 탄창 눈금 ---
        // 잔탄이 탄창의 1/4 아래면 색으로 알린다.
        bool low = ammo <= Mathf.Max(1, WeaponConfig.MagSize / 4);
        Color loaded = reloading ? ReloadCol : (low ? SegLow : SegLoaded);

        for (int i = 0; i < _magSegments.Length; i++)
            _magSegments[i].color = (i < ammo) ? loaded : SegEmpty;

        // --- 재장전 진행 막대 ---
        if (_reloadBarGo != null)
        {
            _reloadBarGo.SetActive(reloading);
            if (reloading && _reloadFill != null)
            {
                float t = _reloadStartedAt < 0f
                        ? 0f
                        : Mathf.Clamp01((Time.unscaledTime - _reloadStartedAt)
                                        / WeaponConfig.ReloadSec);
                _reloadFill.sizeDelta = new Vector2(AmmoStripWidth * t, 4f);
            }
        }

        // --- 여분 바 ---
        if (_reserveFill != null)
        {
            float r = WeaponConfig.ReserveAmmo > 0
                    ? Mathf.Clamp01((float)_weapon.Reserve / WeaponConfig.ReserveAmmo)
                    : 0f;
            _reserveFill.sizeDelta = new Vector2(AmmoStripWidth * r, 6f);
            _reserveFillImg.color = _weapon.Reserve > 0
                                  ? new Color(0.65f, 0.68f, 0.72f, 0.85f)
                                  : new Color(0.90f, 0.25f, 0.25f, 0.85f);
        }

        // --- 숫자 (내장 폰트를 찾은 경우만) ---
        if (_ammoText != null)
        {
            _ammoText.text = reloading
                           ? "RELOADING"
                           : $"{ammo} / {_weapon.Reserve}";
            _ammoText.color = reloading ? ReloadCol
                            : (low ? SegLow : new Color(1f, 1f, 1f, 0.9f));
        }
    }

    // -----------------------------------------------------------------
    //  피격 / 사망
    // -----------------------------------------------------------------

    private void HandleHit(bool headshot, bool killed)
    {
        if (_hitmarker == null) return;

        Color c = killed ? new Color(1f, 0.25f, 0.25f)
                : headshot ? new Color(1f, 0.85f, 0.25f)
                           : Color.white;

        foreach (var img in _hitmarkerParts) img.color = c;

        if (_hitmarkerCo != null) StopCoroutine(_hitmarkerCo);
        _hitmarkerCo = StartCoroutine(ShowHitmarker(killed ? 0.35f : 0.15f));
    }

    private IEnumerator ShowHitmarker(float sec)
    {
        _hitmarker.SetActive(true);
        yield return new WaitForSeconds(sec);
        _hitmarker.SetActive(false);
        _hitmarkerCo = null;
    }

    private void HandleDead(bool dead)
    {
        if (_deathOverlay != null)
            _deathOverlay.gameObject.SetActive(dead);

        if (existingCrosshair != null)
            existingCrosshair.SetActive(!dead);

        if (_crosshairGo != null)
            _crosshairGo.SetActive(!dead);

        // 사망 중에는 재장전 진행 표시가 남지 않도록 정리한다.
        if (dead)
        {
            _reloadStartedAt = -1f;
            _wasReloading = false;
            if (_reloadBarGo != null) _reloadBarGo.SetActive(false);
        }

        if (_cam == null) return;

        if (dead)
        {
            // 카메라를 분리해 부감으로 전환한다.
            _cam.transform.SetParent(null, true);
            _deathCamActive = true;
        }
        else
        {
            _deathCamActive = false;
            _cam.transform.SetParent(transform, false);
            _cam.transform.localPosition = WeaponConfig.EyeOffset;
            _cam.transform.localRotation = Quaternion.identity;
        }
    }

    // -----------------------------------------------------------------
    //  UI 생성
    // -----------------------------------------------------------------

    private void BuildUI()
    {
        var canvasGo = new GameObject("CombatHUD");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        canvasGo.AddComponent<GraphicRaycaster>();

        Font font = LoadBuiltinFont();
        if (font == null)
            Debug.Log("[HUD] 내장 폰트를 찾지 못했다. 탄약은 눈금으로만, K/D 는 표시하지 않는다.");

        BuildHealthBar(canvasGo.transform);
        BuildAmmo(canvasGo.transform, font);
        BuildScore(canvasGo.transform, font);

        // --- 크로스헤어 (스프라이트를 넣은 경우만) ---
        if (crosshairSprite != null)
        {
            var chRect = NewRect("Crosshair", canvasGo.transform);
            Center(chRect, new Vector2(crosshairSize, crosshairSize));
            var chImg = chRect.gameObject.AddComponent<Image>();
            chImg.sprite = crosshairSprite;
            chImg.raycastTarget = false;
            _crosshairGo = chRect.gameObject;
        }

        BuildHitmarker(canvasGo.transform);

        // --- 사망 오버레이 ---
        var ovRect = NewRect("DeathOverlay", canvasGo.transform);
        ovRect.anchorMin = Vector2.zero;
        ovRect.anchorMax = Vector2.one;
        ovRect.offsetMin = Vector2.zero;
        ovRect.offsetMax = Vector2.zero;
        _deathOverlay = ovRect.gameObject.AddComponent<Image>();
        _deathOverlay.color = new Color(0.6f, 0f, 0f, 0.28f);
        _deathOverlay.raycastTarget = false;
        ovRect.gameObject.SetActive(false);
    }

    private void BuildHealthBar(Transform parent)
    {
        var barBg = NewRect("HealthBarBG", parent);
        AnchorBottomLeft(barBg, new Vector2(40f, 40f), new Vector2(_healthBarWidth, 18f));
        var bgImg = barBg.gameObject.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.55f);
        bgImg.raycastTarget = false;

        _healthFill = NewRect("HealthBarFill", barBg);
        _healthFill.anchorMin = new Vector2(0f, 0.5f);
        _healthFill.anchorMax = new Vector2(0f, 0.5f);
        _healthFill.pivot = new Vector2(0f, 0.5f);
        _healthFill.anchoredPosition = Vector2.zero;
        _healthFill.sizeDelta = new Vector2(_healthBarWidth, 18f);
        _healthFillImg = _healthFill.gameObject.AddComponent<Image>();
        _healthFillImg.color = new Color(0.30f, 0.85f, 0.35f);
        _healthFillImg.raycastTarget = false;
    }

    /// <summary>
    /// 우상단 K/D. 폰트가 없으면 만들지 않는다.
    /// 숫자는 눈금이나 막대로 대체할 수 없기 때문이다.
    /// </summary>
    private void BuildScore(Transform parent, Font font)
    {
        if (font == null) return;

        var rect = NewRect("ScoreText", parent);
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-40f, -30f);
        rect.sizeDelta = new Vector2(220f, 32f);

        _scoreText = rect.gameObject.AddComponent<Text>();
        _scoreText.font = font;
        _scoreText.fontSize = 24;
        _scoreText.alignment = TextAnchor.MiddleRight;
        _scoreText.color = new Color(1f, 1f, 1f, 0.9f);
        _scoreText.raycastTarget = false;
        _scoreText.text = "K 0   D 0";
    }

    /// <summary>
    /// 우하단 탄약 표시를 만든다.
    ///
    ///   위   숫자 "30 / 150"        (내장 폰트를 찾은 경우만)
    ///   중   탄창 눈금 MagSize 칸
    ///   아래 재장전 진행 / 여분 바
    /// </summary>
    private void BuildAmmo(Transform parent, Font font)
    {
        // --- 배경 ---
        var bg = NewRect("AmmoBG", parent);
        AnchorBottomRight(bg, new Vector2(-40f, 40f),
                          new Vector2(AmmoStripWidth, AmmoStripHeight));
        var bgImg = bg.gameObject.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.55f);
        bgImg.raycastTarget = false;

        // --- 탄창 눈금 ---
        // MagSize 에서 칸 수를 유도하므로 상수를 바꿔도 따라온다.
        int n = Mathf.Max(1, WeaponConfig.MagSize);
        float segW = (AmmoStripWidth - AmmoSegGap * (n - 1)) / n;

        _magSegments = new Image[n];
        for (int i = 0; i < n; i++)
        {
            var seg = NewRect($"Seg{i}", bg);
            seg.anchorMin = new Vector2(0f, 0.5f);
            seg.anchorMax = new Vector2(0f, 0.5f);
            seg.pivot = new Vector2(0f, 0.5f);
            seg.anchoredPosition = new Vector2(i * (segW + AmmoSegGap), 0f);
            seg.sizeDelta = new Vector2(segW, AmmoStripHeight - 6f);

            var img = seg.gameObject.AddComponent<Image>();
            img.color = SegLoaded;
            img.raycastTarget = false;
            _magSegments[i] = img;
        }

        // --- 재장전 진행 막대 (눈금 바로 아래) ---
        var rlBg = NewRect("ReloadBG", parent);
        AnchorBottomRight(rlBg, new Vector2(-40f, 28f), new Vector2(AmmoStripWidth, 4f));
        var rlBgImg = rlBg.gameObject.AddComponent<Image>();
        rlBgImg.color = new Color(0f, 0f, 0f, 0.45f);
        rlBgImg.raycastTarget = false;
        _reloadBarGo = rlBg.gameObject;

        _reloadFill = NewRect("ReloadFill", rlBg);
        _reloadFill.anchorMin = new Vector2(0f, 0.5f);
        _reloadFill.anchorMax = new Vector2(0f, 0.5f);
        _reloadFill.pivot = new Vector2(0f, 0.5f);
        _reloadFill.anchoredPosition = Vector2.zero;
        _reloadFill.sizeDelta = new Vector2(0f, 4f);
        var rlImg = _reloadFill.gameObject.AddComponent<Image>();
        rlImg.color = ReloadCol;
        rlImg.raycastTarget = false;
        rlBg.gameObject.SetActive(false);

        // --- 여분 바 ---
        var rsBg = NewRect("ReserveBG", parent);
        AnchorBottomRight(rsBg, new Vector2(-40f, 18f), new Vector2(AmmoStripWidth, 6f));
        var rsBgImg = rsBg.gameObject.AddComponent<Image>();
        rsBgImg.color = new Color(0f, 0f, 0f, 0.45f);
        rsBgImg.raycastTarget = false;

        _reserveFill = NewRect("ReserveFill", rsBg);
        _reserveFill.anchorMin = new Vector2(0f, 0.5f);
        _reserveFill.anchorMax = new Vector2(0f, 0.5f);
        _reserveFill.pivot = new Vector2(0f, 0.5f);
        _reserveFill.anchoredPosition = Vector2.zero;
        _reserveFill.sizeDelta = new Vector2(AmmoStripWidth, 6f);
        _reserveFillImg = _reserveFill.gameObject.AddComponent<Image>();
        _reserveFillImg.color = new Color(0.65f, 0.68f, 0.72f, 0.85f);
        _reserveFillImg.raycastTarget = false;

        // --- 숫자 (있으면) ---
        if (font == null) return;

        var txtRect = NewRect("AmmoText", parent);
        AnchorBottomRight(txtRect, new Vector2(-40f, 74f),
                          new Vector2(AmmoStripWidth, 28f));
        _ammoText = txtRect.gameObject.AddComponent<Text>();
        _ammoText.font = font;
        _ammoText.fontSize = 24;
        _ammoText.alignment = TextAnchor.MiddleRight;
        _ammoText.color = new Color(1f, 1f, 1f, 0.9f);
        _ammoText.raycastTarget = false;
        _ammoText.text = $"{WeaponConfig.MagSize} / {WeaponConfig.ReserveAmmo}";
    }

    /// <summary>
    /// 내장 폰트를 찾는다. Unity 2022.2 부터 Arial.ttf 가 빠지고
    /// LegacyRuntime.ttf 로 대체됐으므로 둘 다 시도한다.
    /// 못 찾으면 null 을 돌려주고 호출부가 눈금만 그린다.
    /// </summary>
    private static Font LoadBuiltinFont()
    {
        string[] names = { "LegacyRuntime.ttf", "Arial.ttf" };
        foreach (string n in names)
        {
            try
            {
                var f = Resources.GetBuiltinResource<Font>(n);
                if (f != null) return f;
            }
            catch { /* 버전에 따라 예외가 난다 */ }
        }
        return null;
    }

    private void BuildHitmarker(Transform parent)
    {
        _hitmarker = new GameObject("Hitmarker");
        var hmRect = _hitmarker.AddComponent<RectTransform>();
        hmRect.SetParent(parent, false);
        Center(hmRect, new Vector2(40f, 40f));

        _hitmarkerParts = new Image[4];
        // 중앙에서 대각선 네 방향으로 뻗는 짧은 선
        Vector2[] pos = {
            new Vector2( 10f,  10f), new Vector2(-10f,  10f),
            new Vector2( 10f, -10f), new Vector2(-10f, -10f),
        };
        float[] rot = { 45f, -45f, -45f, 45f };

        for (int i = 0; i < 4; i++)
        {
            var part = NewRect($"Part{i}", hmRect);
            part.anchorMin = part.anchorMax = new Vector2(0.5f, 0.5f);
            part.pivot = new Vector2(0.5f, 0.5f);
            part.anchoredPosition = pos[i];
            part.sizeDelta = new Vector2(12f, 2.5f);
            part.localRotation = Quaternion.Euler(0f, 0f, rot[i]);
            _hitmarkerParts[i] = part.gameObject.AddComponent<Image>();
            _hitmarkerParts[i].color = Color.white;
            _hitmarkerParts[i].raycastTarget = false;
        }
        _hitmarker.SetActive(false);
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>좌하단 기준 배치. offset 은 화면 왼쪽 아래에서의 거리.</summary>
    private static void AnchorBottomLeft(RectTransform rt, Vector2 offset, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = offset;
        rt.sizeDelta = size;
    }

    /// <summary>우하단 기준 배치. offset.x 는 음수로 준다.</summary>
    private static void AnchorBottomRight(RectTransform rt, Vector2 offset, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = offset;
        rt.sizeDelta = size;
    }

    private static void Center(RectTransform rt, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
    }
}