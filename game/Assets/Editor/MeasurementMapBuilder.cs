#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// W7 오탐 측정용 맵 생성기.
///
///   Anticheat > Build W7 Measurement Map      맵 생성
///   Anticheat > Log Hitbox Heights            Play Mode 에서 Yb / Yh / Eye 실측
///
/// 설계 원칙
///   1. 모든 지오메트리는 CreatePrimitive(Cube) 로만 만든다.
///      → 보이는 메시와 콜라이더가 구조적으로 100% 일치한다.
///        (장식 메시와 박스 콜라이더가 어긋나면 사람은 보이는 대로 엄폐하고
///         V-LOS 는 콜라이더로 판정해서, 검증기 버그가 아닌 맵 버그로 오탐이 난다)
///   2. 전부 Default(0) 레이어. 레이캐스트 마스크가 World+Hitbox 이므로
///      다른 레이어에 들어가면 그 오브젝트는 차폐로 세지 않는다.
///   3. 엄폐물 높이는 실측 히트박스에서 역산한다. 임의의 예쁜 숫자를 쓰지 않는다.
///   4. 고저차 없음. 바닥은 y=0 단일 평면.
///      경사·단차가 있으면 눈높이에서 몸통 중심으로 가는 레이가 지형을 스쳐
///      S1 개활지에서 설명 불가능한 차폐가 발생한다.
///
/// PlayerController 와의 계약
///   Ground 는 중심 y=-0.5 / 두께 1 이므로 윗면이 정확히 y=0.
///   FLOOR_SURFACE_Y = 0, RestY = 0 + 0.08 + 1.0 - 0 = 1.08 (실측 확인 완료)
/// </summary>
public static class MeasurementMapBuilder
{
    const string RootName = "__MAP_W7";
    const string MatFolder = "Assets/Materials/W7Map";

    // ═════════════════════════════════════════════════════════════
    //  실측값  (Log Hitbox Heights, 2026-09-07 확정)
    //
    //    root.position.y = 1.0800   CC height=2.00 center=(0,0,0) skin=0.080
    //    Eye  world Y    = 1.8300   (EyeOffset = 0.75)
    //    Body centerY    = 0.8800   범위 0.080 ~ 1.680
    //    Head centerY    = 1.8300   범위 1.580 ~ 2.080
    //
    //  ★ 머리 중심이 눈높이와 정확히 같다 (둘 다 1.830).
    //    머리로 가는 조준선이 완벽한 수평선이 된다는 뜻이다.
    //    → 1.83 미만 엄폐물은 거리와 무관하게 머리를 절대 못 가린다.
    //    → 1.83 이상이면 즉시 가린다. 중간 지대가 없다.
    //
    //    몸통까지는 눈에서 0.95m 낙차라 기울기가 충분하다.
    //    오탐 조건("몸통만 가림")이 깨끗하게 만들어진다.
    // ═════════════════════════════════════════════════════════════
    const float Yb = 0.88f;   // Body 히트박스 중심 world Y (capsule h1.6 r0.5)
    const float Yh = 1.83f;   // Head 히트박스 중심 world Y (sphere r0.25)
    const float Eye = 1.83f;   // 눈 world Y = root(1.08) + EyeOffset.y(0.75)

    /// <summary>
    /// 스폰 높이. CharacterController Center=(0,0,0) 이므로 스폰 좌표는
    /// "발바닥"이 아니라 "캡슐 중심"이다. y=0 을 주면 절반이 묻힌다.
    /// PlayerController.RestY 와 같은 값이어야 한다.
    /// </summary>
    const float SpawnY = 1.08f;

    /// <summary>
    /// 반차폐 사다리. 눈(1.83) → 몸통(0.88) 낙차 0.95m 를 기준으로 잡았다.
    ///
    ///  높이   몸통 중심                      머리 중심   단일 판정
    ///  ────  ────────────────────────────  ─────────  ──────────────
    ///  0.60  보임                           보임        비차폐 (대조군)
    ///  1.00  표적이 엄폐물 뒤 ~3m 내면 가림   보임        오탐 (경계)
    ///  1.45  표적이 엄폐물 뒤 ~9m 내면 가림   보임        오탐 (주력)
    ///  1.90  가림                           가림        정상 차폐
    ///
    /// 1.45 가 핵심이다. 머리는 1.83 수평선이라 절대 안 가려지는데
    /// 몸통 중심만 가려진다 — 사람 눈에는 완전히 보이는 상대다.
    /// </summary>
    static readonly float[] CoverHeights = { 0.60f, 1.00f, 1.45f, 1.90f };

    const float FullH = 3.0f;   // 완전차폐. 머리 상단(2.08)보다 충분히 높게
    const float WallH = 4.0f;   // 외벽 / 분리벽

    // ═════════════════════════════════════════════════════════════
    //  맵 생성
    // ═════════════════════════════════════════════════════════════

    [MenuItem("Anticheat/Build W7 Measurement Map")]
    public static void Build()
    {
        var old = GameObject.Find(RootName);
        if (old != null)
        {
            if (!EditorUtility.DisplayDialog(
                    "W7 Measurement Map",
                    $"기존 '{RootName}' 을(를) 삭제하고 다시 생성합니다.\n" +
                    "수동으로 추가한 오브젝트가 있으면 함께 사라집니다.",
                    "진행", "취소"))
                return;
            Object.DestroyImmediate(old);
        }

        EnsureFolder(MatFolder);

        var root = new GameObject(RootName);
        root.transform.position = Vector3.zero;
        root.AddComponent<MapManifest>();

        var gGround = Group(root, "00_Ground");
        var gPerim = Group(root, "01_Perimeter");
        var gDiv = Group(root, "02_Divider");
        var gFull = Group(root, "03_FullCover");
        var gHalf = Group(root, "04_HalfCover");
        var gSpawn = Group(root, "05_Spawns");

        // ── 바닥 ──────────────────────────────────────────────────
        // 얇은 Plane 대신 두께 1 의 Box. 상면이 정확히 y=0.
        // X: -45 ~ +30 (75m),  Z: -15 ~ +15 (30m)
        Box(gGround, "Ground", new Vector3(-7.5f, -0.5f, 0f), new Vector3(75f, 1f, 30f), "ground");

        // ── 외벽 (두께 1, 높이 4) ─────────────────────────────────
        Box(gPerim, "Wall_N", new Vector3(-7.5f, WallH * 0.5f, 15.5f), new Vector3(77f, WallH, 1f), "wall");
        Box(gPerim, "Wall_S", new Vector3(-7.5f, WallH * 0.5f, -15.5f), new Vector3(77f, WallH, 1f), "wall");
        Box(gPerim, "Wall_W", new Vector3(-45.5f, WallH * 0.5f, 0f), new Vector3(1f, WallH, 30f), "wall");
        Box(gPerim, "Wall_E", new Vector3(30.5f, WallH * 0.5f, 0f), new Vector3(1f, WallH, 30f), "wall");

        // ── 분리벽 X=0 ────────────────────────────────────────────
        // Zone A 를 볼록(convex)하게 닫아 S1 의 "V-LOS 0건"을 기하학적으로 보장한다.
        // 볼록한 영역 내부 두 점을 잇는 선분은 절대 밖으로 나가지 않으므로,
        // 개활지에서 차폐가 발생하면 그건 튜닝이 아니라 버그다.
        // 통로 2곳: Z -7~-3, Z +3~+7  (각 4m) — 코너 피킹 지점.
        Box(gDiv, "Div_S", new Vector3(0f, WallH * 0.5f, -11f), new Vector3(1f, WallH, 8f), "wall");
        Box(gDiv, "Div_M", new Vector3(0f, WallH * 0.5f, 0f), new Vector3(1f, WallH, 6f), "wall");
        Box(gDiv, "Div_N", new Vector3(0f, WallH * 0.5f, 11f), new Vector3(1f, WallH, 8f), "wall");

        // ── Zone B 완전차폐 ───────────────────────────────────────
        // X=7 과 X=26 에 각각 4m 폭 출입구. 둘 사이 19m 가 클리어 레인.
        Box(gFull, "FW_A", new Vector3(7f, FullH * 0.5f, -7f), new Vector3(0.5f, FullH, 10f), "full");
        Box(gFull, "FW_B", new Vector3(7f, FullH * 0.5f, 7f), new Vector3(0.5f, FullH, 10f), "full");
        Box(gFull, "FW_D", new Vector3(26f, FullH * 0.5f, -7f), new Vector3(0.5f, FullH, 10f), "full");
        Box(gFull, "FW_E", new Vector3(26f, FullH * 0.5f, 7f), new Vector3(0.5f, FullH, 10f), "full");
        // 레인 밖 측면 블록. 선회/추적 및 완전차폐 대기용.
        Box(gFull, "FW_C", new Vector3(16f, FullH * 0.5f, -8f), new Vector3(4f, FullH, 4f), "full");
        Box(gFull, "FW_F", new Vector3(16f, FullH * 0.5f, 8f), new Vector3(4f, FullH, 4f), "full");

        // ── 반차폐 사다리 2열 ─────────────────────────────────────
        // 두께 1(X) × 폭 3(Z). 주 교전축이 X 이므로 X 에 수직으로 세운다.
        // 두 열의 Z 배치를 뒤집어, X 축으로 마주 보면 서로 다른 높이가 걸리게 한다.
        float[] zL1 = { -9f, -3f, 3f, 9f };
        float[] zL2 = { 9f, 3f, -3f, -9f };
        for (int i = 0; i < CoverHeights.Length; i++)
        {
            float h = CoverHeights[i];
            string key = "half" + i;
            int tag = Mathf.RoundToInt(h * 100f);

            Box(gHalf, $"HC_{tag}_L1", new Vector3(12f, h * 0.5f, zL1[i]), new Vector3(1f, h, 3f), key);
            Box(gHalf, $"HC_{tag}_L2", new Vector3(22f, h * 0.5f, zL2[i]), new Vector3(1f, h, 3f), key);
        }

        // ── 스폰 마커 (콜라이더 없음) ─────────────────────────────
        // y = SpawnY = 캡슐 중심 안착 높이. 그대로 transform.position 에 넣으면 된다.
        // 전부 Zone A — S1 이 Zone B 리스폰으로 오염되지 않게 한다.
        Marker(gSpawn, "SP_A1", new Vector3(-38f, SpawnY, -11f));
        Marker(gSpawn, "SP_A2", new Vector3(-38f, SpawnY, 11f));
        Marker(gSpawn, "SP_B1", new Vector3(-8f, SpawnY, -11f));
        Marker(gSpawn, "SP_B2", new Vector3(-8f, SpawnY, 11f));

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;

        Verify(root);
    }

    static void Verify(GameObject root)
    {
        var r = MapManifest.Scan(root.transform);

        var sb = new StringBuilder();
        sb.AppendLine($"[W7MAP] {MapManifest.MapVersion} 생성 완료");
        sb.AppendLine($"  콜라이더 {r.ColliderCount} 개 · geomHash={r.GeomHash:X8}");
        sb.AppendLine($"  범위 {r.Bounds.size.x:F1} x {r.Bounds.size.z:F1} m · 바닥 상면 y=0");
        sb.AppendLine($"  실측 Eye={Eye:F2}  Body={Yb:F2}  Head={Yh:F2}  SpawnY={SpawnY:F2}");
        sb.AppendLine($"  눈→몸통 낙차 = {(Eye - Yb):F2} m");
        sb.AppendLine("  ── 반차폐 사다리 판정표 ──────────────────────");
        sb.AppendLine("     높이   몸통(0.88)   머리(1.83)   단일 판정");

        foreach (float h in CoverHeights)
        {
            // 사수가 엄폐물에서 10m 떨어졌을 때, 표적이 엄폐물 뒤 몇 m 안에
            // 있어야 몸통 중심이 가려지는지. dt < ds * (Eye-h)/(h-Yb) 의 역산.
            string bodyNote;
            if (h <= Yb) bodyNote = "보임";
            else if (h >= Eye) bodyNote = "가림";
            else bodyNote = $"뒤 {10f * (h - Yb) / (Eye - h):F1}m 내면 가림";

            string headNote = h >= Yh ? "가림" : "보임";
            string verdict = (h > Yb && h < Yh) ? "★오탐" : (h >= Yh ? "정상차폐" : "비차폐");

            sb.AppendLine($"     {h,4:F2}   {bodyNote,-22} {headNote,-10} {verdict}");
        }

        sb.AppendLine("  → geomHash 를 서버 로그의 [MAP] 줄과 대조하세요.");
        Debug.Log(sb.ToString());

        if (r.NonMapLayerCount > 0)
            Debug.LogError($"[W7MAP] Default 레이어가 아닌 콜라이더 {r.NonMapLayerCount} 개. V-LOS 무효.");
        else
            Debug.Log("[W7MAP] 레이어 검사 통과 — 전부 Default(World).");
    }

    // ═════════════════════════════════════════════════════════════
    //  히트박스 실측
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Play Mode(Host 권장)에서 PlayerCharacter(Clone) 루트를 선택하고 실행한다.
    /// 클라이언트 전용으로 접속하면 히트박스가 없어 콜라이더 목록이 비어 나온다.
    /// </summary>
    [MenuItem("Anticheat/Log Hitbox Heights")]
    static void LogHitboxHeights()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[W7MAP] Play Mode 에서 실행하세요. 플레이어가 바닥에 안착한 뒤여야 유효합니다.");
            return;
        }

        var go = Selection.activeGameObject;
        if (go == null)
        {
            Debug.LogWarning("[W7MAP] Hierarchy 에서 PlayerCharacter(Clone) 루트를 선택한 뒤 실행하세요.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[W7MAP] 히트박스 실측 — {go.name}");
        sb.AppendLine($"  root.position.y = {go.transform.position.y:F4}   layer={LayerMask.LayerToName(go.layer)}");

        var cc = go.GetComponentInChildren<CharacterController>();
        if (cc == null)
        {
            sb.AppendLine("  CharacterController 없음 — 루트를 잘못 선택했을 수 있습니다.");
        }
        else
        {
            float ccBottom = cc.transform.position.y + cc.center.y - cc.height * 0.5f;
            sb.AppendLine($"  CC  height={cc.height:F2}  radius={cc.radius:F2}  " +
                          $"center={cc.center}  skinWidth={cc.skinWidth:F3}");
            sb.AppendLine($"  CC  캡슐 바닥 y = {ccBottom:F4}");
            sb.AppendLine($"      → +{cc.skinWidth:F3} 부근이면 정상. 음수면 묻힌 것, 훨씬 크면 떠 있는 것.");
            sb.AppendLine($"  권장 SpawnY = {(0f + cc.skinWidth + cc.height * 0.5f - cc.center.y):F4}");
        }

        float eyeY = go.transform.position.y + WeaponConfig.EyeOffset.y;
        sb.AppendLine($"  Eye world Y = {eyeY:F4}   (EyeOffset={WeaponConfig.EyeOffset})");

        sb.AppendLine("  ── 콜라이더 ──────────────────────────────────");
        bool any = false;
        foreach (var c in go.GetComponentsInChildren<Collider>(true))
        {
            if (c is CharacterController) continue;
            any = true;
            var b = c.bounds;
            sb.AppendLine($"    {c.name,-16} layer={LayerMask.LayerToName(c.gameObject.layer),-8} " +
                          $"centerY={b.center.y,7:F3}   {b.min.y,7:F3} ~ {b.max.y,7:F3}");
            sb.AppendLine($"      눈과의 낙차 = {(eyeY - b.center.y):F3} m");
        }
        if (!any)
            sb.AppendLine("    (없음) — 클라이언트 전용 접속이면 히트박스가 서버에만 있습니다. Host 로 실행하세요.");

        sb.AppendLine("  ── 반영 ─────────────────────────────────────");
        sb.AppendLine("    Body centerY → 이 파일 상단의 Yb");
        sb.AppendLine("    Head centerY → 이 파일 상단의 Yh");
        sb.AppendLine("    Eye  world Y → 이 파일 상단의 Eye");
        sb.AppendLine("    수정 후 Build W7 Measurement Map 재실행 + geomHash 갱신.");

        Debug.Log(sb.ToString());
    }

    // ═════════════════════════════════════════════════════════════
    //  헬퍼
    // ═════════════════════════════════════════════════════════════

    static GameObject Group(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    /// <summary>size 는 월드 크기. Cube 프리미티브는 1x1x1 이므로 localScale 과 동일.</summary>
    static GameObject Box(GameObject parent, string name, Vector3 center, Vector3 size, string matKey)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // 메시 + BoxCollider 동시 생성
        go.name = name;
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = center;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = size;
        go.layer = MapManifest.MapLayer;

        // ContributeGI 는 켜지 않는다 — 측정 맵에 라이트맵 베이크는 불필요하고 느리다.
        GameObjectUtility.SetStaticEditorFlags(go,
            StaticEditorFlags.BatchingStatic |
            StaticEditorFlags.OccluderStatic |
            StaticEditorFlags.OccludeeStatic);

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial = GetMaterial(matKey);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }
        return go;
    }

    static void Marker(GameObject parent, string name, Vector3 pos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos;
    }

    static readonly Dictionary<string, Color> Palette = new Dictionary<string, Color>
    {
        { "ground", new Color(0.34f, 0.36f, 0.38f) },
        { "wall",   new Color(0.22f, 0.24f, 0.29f) },
        { "full",   new Color(0.46f, 0.22f, 0.22f) },   // 빨강  완전차폐
        { "half0",  new Color(0.20f, 0.46f, 0.30f) },   // 초록  0.60  대조군
        { "half1",  new Color(0.36f, 0.54f, 0.24f) },   // 연두  1.00  경계
        { "half2",  new Color(0.60f, 0.54f, 0.20f) },   // 노랑  1.45  오탐 주력
        { "half3",  new Color(0.62f, 0.36f, 0.18f) },   // 주황  1.90  정상차폐
    };

    static Material GetMaterial(string key)
    {
        string path = $"{MatFolder}/W7_{key}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;

        m = new Material(FindShader());
        Color c = Palette.TryGetValue(key, out var col) ? col : Color.gray;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static Shader FindShader()
    {
        string[] candidates = { "Universal Render Pipeline/Lit", "HDRP/Lit", "Standard" };
        foreach (var n in candidates)
        {
            var s = Shader.Find(n);
            if (s != null) return s;
        }
        return Shader.Find("Sprites/Default");
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        string leaf = Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif