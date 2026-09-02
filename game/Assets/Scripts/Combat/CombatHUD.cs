// =====================================================================
//  CombatHUD.cs
//  경로: game/Assets/Scripts/Combat/CombatHUD.cs
//
//  소유 클라이언트 전용 전투 UI. PlayerCharacter 프리팹에 붙인다.
//
//  포함
//   - 체력바 (좌하단, 잔량에 따라 색 변화)
//   - 히트마커 (중앙 X, 명중 시 잠깐 표시 / 헤드샷은 노랑 / 킬은 빨강)
//   - 사망 오버레이 + 3인칭 부감 카메라
//
//  UI 를 코드로 생성하는 이유
//   프리팹/씬 편집 단계를 줄이기 위해서다. 폰트 리소스에 의존하지 않도록
//   텍스트를 쓰지 않고 사각형과 색으로만 표현한다.
//   숫자 표시가 필요하면 나중에 TMP 를 얹으면 된다.
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
    private Camera       _cam;

    private RectTransform _healthFill;
    private Image         _healthFillImg;
    private float         _healthBarWidth = 240f;

    private GameObject _hitmarker;
    private Image[]    _hitmarkerParts;
    private Coroutine  _hitmarkerCo;

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
        _cam    = GetComponentInChildren<Camera>(true);

        BuildUI();

        if (_weapon != null) _weapon.OnHitConfirmed += HandleHit;
        if (_health != null) _health.OnDeadChanged  += HandleDead;
    }

    public override void OnNetworkDespawn()
    {
        if (_weapon != null) _weapon.OnHitConfirmed -= HandleHit;
        if (_health != null) _health.OnDeadChanged  -= HandleDead;
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
            _healthFillImg.color  = ratio > 0.5f ? new Color(0.30f, 0.85f, 0.35f)
                                  : ratio > 0.25f ? new Color(0.95f, 0.75f, 0.20f)
                                                  : new Color(0.90f, 0.25f, 0.25f);
        }

        if (_deathCamActive && _cam != null)
        {
            // 시체를 계속 내려다본다.
            Vector3 target = transform.position;
            _cam.transform.position = target + deathCamOffset;
            _cam.transform.LookAt(target + Vector3.up * 0.5f);
        }
    }

    // -----------------------------------------------------------------
    //  피격 / 사망
    // -----------------------------------------------------------------

    private void HandleHit(bool headshot, bool killed)
    {
        if (_hitmarker == null) return;

        Color c = killed   ? new Color(1f, 0.25f, 0.25f)
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

        if (_crosshairGo != null)                 // ← 추가
            _crosshairGo.SetActive(!dead);

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
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        canvasGo.AddComponent<GraphicRaycaster>();

        // --- 체력바 ---
        var barBg = NewRect("HealthBarBG", canvasGo.transform);
        Anchor(barBg, new Vector2(0f, 0f), new Vector2(0f, 0f),
               new Vector2(40f, 40f), new Vector2(_healthBarWidth, 18f));
        var bgImg = barBg.gameObject.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.55f);

        _healthFill = NewRect("HealthBarFill", barBg);
        _healthFill.anchorMin = new Vector2(0f, 0.5f);
        _healthFill.anchorMax = new Vector2(0f, 0.5f);
        _healthFill.pivot     = new Vector2(0f, 0.5f);
        _healthFill.anchoredPosition = Vector2.zero;
        _healthFill.sizeDelta = new Vector2(_healthBarWidth, 18f);
        _healthFillImg = _healthFill.gameObject.AddComponent<Image>();
        _healthFillImg.color = new Color(0.30f, 0.85f, 0.35f);

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

        // --- 히트마커 ---
        _hitmarker = new GameObject("Hitmarker");
        var hmRect = _hitmarker.AddComponent<RectTransform>();
        hmRect.SetParent(canvasGo.transform, false);
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
            part.pivot     = new Vector2(0.5f, 0.5f);
            part.anchoredPosition = pos[i];
            part.sizeDelta = new Vector2(12f, 2.5f);
            part.localRotation = Quaternion.Euler(0f, 0f, rot[i]);
            _hitmarkerParts[i] = part.gameObject.AddComponent<Image>();
            _hitmarkerParts[i].color = Color.white;
        }
        _hitmarker.SetActive(false);

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

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    private static void Anchor(RectTransform rt, Vector2 min, Vector2 max,
                               Vector2 offset, Vector2 size)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.pivot     = new Vector2(0f, 0.5f);
        rt.anchoredPosition = offset;
        rt.sizeDelta = size;
    }

    private static void Center(RectTransform rt, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
    }
}
