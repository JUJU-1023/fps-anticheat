using UnityEngine;

/// <summary>
/// W7 측정 맵의 신원(version + 지오메트리 해시)을 서버 기동 시 로그로 남긴다.
///
/// 왜 필요한가:
///   맵을 바꾸면 씬 데이터(level*, sharedassets*)가 바뀌지만 Assembly-CSharp.dll 은
///   그대로일 수 있다. 기존 "DLL 타임스탬프 확인" 절차로는 맵이 옛날 것인 채로
///   배포된 걸 절대 못 잡는다. 그 상태로 S2 를 돌리면 V-LOS 위반 0건이 나오는데,
///   그건 성공이 아니라 측정 실패다.
///
/// 사용:
///   맵 루트(__MAP_W7)에 붙인다. MeasurementMapBuilder 가 자동으로 붙여준다.
/// </summary>
[DefaultExecutionOrder(-500)]
public class MapManifest : MonoBehaviour
{
    /// <summary>맵 구조를 바꿀 때마다 올린다. docs/w7_fp_measurement.md 에 기록할 것.</summary>
    public const string MapVersion = "w7-map-1.0";

    /// <summary>맵 지오메트리가 있어야 하는 레이어. Default(0) = World.</summary>
    public const int MapLayer = 0;

    public struct ScanResult
    {
        public int     ColliderCount;
        public int     NonMapLayerCount;
        public int     GeomHash;
        public Bounds  Bounds;
    }

    void Start()
    {
        var r = Scan(transform);

        Debug.Log(
            $"[MAP] version={MapVersion} colliders={r.ColliderCount} " +
            $"geomHash={r.GeomHash:X8} " +
            $"extent={r.Bounds.size.x:F1}x{r.Bounds.size.z:F1} " +
            $"nonMapLayer={r.NonMapLayerCount}");

        if (r.NonMapLayerCount > 0)
        {
            Debug.LogError(
                $"[MAP] 맵 콜라이더 {r.NonMapLayerCount} 개가 Default 레이어가 아닙니다. " +
                $"레이캐스트 마스크(World+Hitbox)에서 빠지므로 V-LOS 판정 전체가 무효입니다.");
        }
    }

    /// <summary>
    /// 자식 콜라이더를 훑어 개수·레이어 위반·지오메트리 해시·바운즈를 계산한다.
    /// 에디터 빌더와 런타임이 같은 함수를 쓰므로 두 해시는 반드시 일치해야 한다.
    /// </summary>
    public static ScanResult Scan(Transform root)
    {
        var cols = root.GetComponentsInChildren<Collider>(true);

        var res = new ScanResult
        {
            ColliderCount = cols.Length,
            Bounds        = new Bounds(root.position, Vector3.zero),
            GeomHash      = 17,
        };

        // 이름 순으로 정렬해 계층 순서 변화에 해시가 흔들리지 않게 한다.
        System.Array.Sort(cols, (a, b) => string.CompareOrdinal(a.name, b.name));

        foreach (var c in cols)
        {
            if (c.gameObject.layer != MapLayer) res.NonMapLayerCount++;

            var b = c.bounds;
            res.Bounds.Encapsulate(b);

            unchecked
            {
                int h = res.GeomHash;
                h = h * 31 + StableHash(c.name);
                h = h * 31 + Mathf.RoundToInt(b.center.x * 100f);
                h = h * 31 + Mathf.RoundToInt(b.center.y * 100f);
                h = h * 31 + Mathf.RoundToInt(b.center.z * 100f);
                h = h * 31 + Mathf.RoundToInt(b.size.x   * 100f);
                h = h * 31 + Mathf.RoundToInt(b.size.y   * 100f);
                h = h * 31 + Mathf.RoundToInt(b.size.z   * 100f);
                res.GeomHash = h;
            }
        }

        return res;
    }

    /// <summary>
    /// string.GetHashCode 는 런타임에 따라 randomized 될 수 있어 직접 계산한다.
    /// 에디터와 리눅스 서버 빌드에서 같은 값이 나와야 배포 검증이 성립한다.
    /// </summary>
    static int StableHash(string s)
    {
        int h = 23;
        for (int i = 0; i < s.Length; i++) h = unchecked(h * 31 + s[i]);
        return h;
    }
}
