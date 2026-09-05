#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// W7 오탐 측정용 맵 생성기.  메뉴: Anticheat > Build W7 Measurement Map
///
/// 설계 원칙
///   1. 모든 지오메트리는 CreatePrimitive(Cube) 로만 만든다.
///      → 보이는 메시와 콜라이더가 구조적으로 100% 일치한다.
///        (장식 메시와 박스 콜라이더가 어긋나면 사람은 보이는 대로 엄폐하고
///         V-LOS 는 콜라이더로 판정해서, 검증기 버그가 아닌 맵 버그로 오탐이 난다)
///   2. 전부 Default(0) 레이어. 레이캐스트 마스크가 World+Hitbox 이므로
///      다른 레이어에 들어가면 그 오브젝트는 차폐로 세지 않는다.
///   3. 높이가 아니라 "히트박스 기준 상대 높이"로 정의한다.
///      Yb / Yh 만 실측값으로 바꾸면 사다리 전체가 따라 움직인다.
///   4. 고저차 없음. 바닥은 y=0 단일 평면.
///      경사·단차가 있으면 눈높이에서 몸통 중심으로 가는 레이가 지형을 스쳐
///      S1 개활지에서 설명 불가능한 차폐가 발생한다.
/// </summary>
public static class MeasurementMapBuilder
{
    const string RootName  = "__MAP_W7";
    const string MatFolder = "Assets/Materials/W7Map";

    // ─────────────────────────────────────────────────────────────
    //  히트박스 기준값  ※ Play Mode 에서 실측해 반드시 검증할 것
    //     Body : capsule h1.6 r0.5  → 중심 world Y
    //     Head : sphere  r0.25      → 중심 world Y
    //  값이 다르면 여기만 고치면 된다. 히트박스는 절대 건드리지 않는다.
    // ─────────────────────────────────────────────────────────────
    const float Yb = 1.00f;   // Body 히트박스 중심
    const float Yh = 1.70f;   // Head 히트박스 중심

    /// <summary>반차폐 사다리. 몸통 중심(Yb)을 사이에 두도록 설계.</summary>
    static readonly float[] CoverHeights =
    {
        Yb - 0.30f,   // 0.70  몸통 아래      → 비차폐 (대조군)
        Yb + 0.10f,   // 1.10  몸통 바로 위    → 경계. 표적이 바짝 붙어야 발생
        Yb + 0.50f,   // 1.50  몸통 가림/머리 보임 → 오탐 주력
        Yb + 0.90f,   // 1.90  몸통·머리 모두 가림 → 수정 후에도 차폐여야 함
    };

    // 완전차폐 높이. 머리 상단(Yh+0.25=1.95)보다 충분히 높게.
    const float FullH = 3.0f;
    const float WallH = 4.0f;   // 외벽 / 분리벽

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
        var gPerim  = Group(root, "01_Perimeter");
        var gDiv    = Group(root, "02_Divider");
        var gFull   = Group(root, "03_FullCover");
        var gHalf   = Group(root, "04_HalfCover");
        var gSpawn  = Group(root, "05_Spawns");

        // ── 바닥 ──────────────────────────────────────────────────
        // 얇은 Plane 대신 두께 1 의 Box. 상면이 정확히 y=0.
        // X: -45 ~ +30 (75m),  Z: -15 ~ +15 (30m)
        Box(gGround, "Ground", new Vector3(-7.5f, -0.5f, 0f), new Vector3(75f, 1f, 30f), "ground");

        // ── 외벽 (두께 1, 높이 4) ─────────────────────────────────
        Box(gPerim, "Wall_N", new Vector3( -7.5f, WallH * 0.5f,  15.5f), new Vector3(77f, WallH,  1f), "wall");
        Box(gPerim, "Wall_S", new Vector3( -7.5f, WallH * 0.5f, -15.5f), new Vector3(77f, WallH,  1f), "wall");
        Box(gPerim, "Wall_W", new Vector3(-45.5f, WallH * 0.5f,   0f  ), new Vector3( 1f, WallH, 30f), "wall");
        Box(gPerim, "Wall_E", new Vector3( 30.5f, WallH * 0.5f,   0f  ), new Vector3( 1f, WallH, 30f), "wall");

        // ── 분리벽 X=0 ────────────────────────────────────────────
        // Zone A 를 볼록(convex)하게 닫아 S1 의 "V-LOS 0건"을 기하학적으로 보장한다.
        // 통로 2곳: Z -7~-3, Z +3~+7  (각 4m) — 코너 피킹 지점.
        Box(gDiv, "Div_S", new Vector3(0f, WallH * 0.5f, -11f), new Vector3(1f, WallH, 8f), "wall");
        Box(gDiv, "Div_M", new Vector3(0f, WallH * 0.5f,   0f), new Vector3(1f, WallH, 6f), "wall");
        Box(gDiv, "Div_N", new Vector3(0f, WallH * 0.5f,  11f), new Vector3(1f, WallH, 8f), "wall");

        // ── Zone B 완전차폐 ───────────────────────────────────────
        // X=7 과 X=26 에 각각 4m 폭 출입구. 둘 사이 19m 가 클리어 레인.
        Box(gFull, "FW_A", new Vector3( 7f, FullH * 0.5f, -7f), new Vector3(0.5f, FullH, 10f), "full");
        Box(gFull, "FW_B", new Vector3( 7f, FullH * 0.5f,  7f), new Vector3(0.5f, FullH, 10f), "full");
        Box(gFull, "FW_D", new Vector3(26f, FullH * 0.5f, -7f), new Vector3(0.5f, FullH, 10f), "full");
        Box(gFull, "FW_E", new Vector3(26f, FullH * 0.5f,  7f), new Vector3(0.5f, FullH, 10f), "full");
        // 레인 밖 측면 블록. 선회/추적 및 완전차폐 대기용.
        Box(gFull, "FW_C", new Vector3(16f, FullH * 0.5f, -8f), new Vector3(4f, FullH, 4f), "full");
        Box(gFull, "FW_F", new Vector3(16f, FullH * 0.5f,  8f), new Vector3(4f, FullH, 4f), "full");

        // ── 반차폐 사다리 2열 ─────────────────────────────────────
        // 두께 1(X) × 폭 3(Z). 주 교전축이 X 이므로 X 에 수직으로 세운다.
        // 두 열의 Z 배치를 뒤집어, X 축으로 마주 보면 서로 다른 높이가 걸리게 한다.
        float[] zL1 = { -9f, -3f,  3f,  9f };
        float[] zL2 = {  9f,  3f, -3f, -9f };
        for (int i = 0; i < CoverHeights.Length; i++)
        {
            float h   = CoverHeights[i];
            string key = "half" + i;
            int    tag = Mathf.RoundToInt(h * 100f);

            Box(gHalf, $"HC_{tag}_L1", new Vector3(12f, h * 0.5f, zL1[i]), new Vector3(1f, h, 3f), key);
            Box(gHalf, $"HC_{tag}_L2", new Vector3(22f, h * 0.5f, zL2[i]), new Vector3(1f, h, 3f), key);
        }

        // ── 스폰 마커 (콜라이더 없음) ─────────────────────────────
        // 전부 Zone A. S1 이 Zone B 리스폰으로 오염되지 않게 한다.
        Marker(gSpawn, "SP_A1", new Vector3(-38f, 0f, -11f));
        Marker(gSpawn, "SP_A2", new Vector3(-38f, 0f,  11f));
        Marker(gSpawn, "SP_B1", new Vector3( -8f, 0f, -11f));
        Marker(gSpawn, "SP_B2", new Vector3( -8f, 0f,  11f));

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;

        Verify(root);
    }

    // ─────────────────────────────────────────────────────────────

    static void Verify(GameObject root)
    {
        var r = MapManifest.Scan(root.transform);

        Debug.Log(
            $"[W7MAP] {MapManifest.MapVersion} 생성 완료\n" +
            $"  콜라이더 {r.ColliderCount} 개 · geomHash={r.GeomHash:X8}\n" +
            $"  범위 {r.Bounds.size.x:F1} x {r.Bounds.size.z:F1} m\n" +
            $"  Yb={Yb:F2}  Yh={Yh:F2}  머리 상단={Yh + 0.25f:F2}\n" +
            $"  반차폐 높이 = {string.Join(" / ", CoverHeights)}\n" +
            $"  → 이 geomHash 를 서버 로그의 [MAP] 줄과 대조하세요.");

        if (r.NonMapLayerCount > 0)
            Debug.LogError($"[W7MAP] Default 레이어가 아닌 콜라이더 {r.NonMapLayerCount} 개. V-LOS 무효.");
        else
            Debug.Log("[W7MAP] 레이어 검사 통과 — 전부 Default(World).");
    }

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
        go.transform.localScale    = size;
        go.layer = MapManifest.MapLayer;

        // ContributeGI 는 켜지 않는다 — 측정 맵에 라이트맵 베이크는 불필요하고 느리다.
        GameObjectUtility.SetStaticEditorFlags(go,
            StaticEditorFlags.BatchingStatic |
            StaticEditorFlags.OccluderStatic |
            StaticEditorFlags.OccludeeStatic);

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial   = GetMaterial(matKey);
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

    // ── 머티리얼 ──────────────────────────────────────────────────

    static readonly Dictionary<string, Color> Palette = new Dictionary<string, Color>
    {
        { "ground", new Color(0.34f, 0.36f, 0.38f) },
        { "wall",   new Color(0.22f, 0.24f, 0.29f) },
        { "full",   new Color(0.46f, 0.22f, 0.22f) },
        { "half0",  new Color(0.20f, 0.46f, 0.30f) },   // 0.70
        { "half1",  new Color(0.36f, 0.54f, 0.24f) },   // 1.10
        { "half2",  new Color(0.60f, 0.54f, 0.20f) },   // 1.50
        { "half3",  new Color(0.62f, 0.36f, 0.18f) },   // 1.90
    };

    static Material GetMaterial(string key)
    {
        string path = $"{MatFolder}/W7_{key}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;

        m = new Material(FindShader());
        Color c = Palette.TryGetValue(key, out var col) ? col : Color.gray;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color"))     m.SetColor("_Color", c);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static Shader FindShader()
    {
        string[] candidates =
        {
            "Universal Render Pipeline/Lit",
            "HDRP/Lit",
            "Standard",
        };
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
        string leaf   = Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif
