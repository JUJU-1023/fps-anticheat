using System.Numerics;

namespace CheatClient.Core;

/// <summary>
/// 위치 → 조준각 변환과 표적 선택.
///
/// 게임의 조준 방향 정의 (PlayerController / 서버 FireHitscan 과 동일)
///   dir = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward
///       = ( sin(yaw)·cos(pitch),  −sin(pitch),  cos(yaw)·cos(pitch) )
///   → yaw   = atan2(dx, dz)
///   → pitch = −atan2(dy, 수평거리)      (양수 = 아래를 봄)
///
/// yaw 는 게임처럼 [0, 360) 으로, pitch 는 ±89 로 자른다.
/// </summary>
public static class AimSolver
{
    // ★ 확인 필요 ★ WeaponConfig.EyeOffset 의 y.
    //   서버 히트스캔 원점 = transform.position + EyeOffset 이라 이 값이 틀리면
    //   가까운 적일수록 pitch 가 어긋난다. 실제 값으로 바꿀 것.
    public const float EyeOffsetY  = 0.75f;

    public const float HeadOffsetY = 0.75f;    // Head 히트박스 중심 1.83 − RestY 1.08
    public const float BodyOffsetY = -0.20f;   // Body 히트박스 중심 0.88 − RestY 1.08

    private const float Rad2Deg = 180f / MathF.PI;

    public static (float yaw, float pitch) AnglesTo(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        float horiz = MathF.Sqrt(d.X * d.X + d.Z * d.Z);

        float yaw = MathF.Atan2(d.X, d.Z) * Rad2Deg;
        if (yaw < 0f) yaw += 360f;

        float pitch = -MathF.Atan2(d.Y, horiz) * Rad2Deg;
        pitch = Math.Clamp(pitch, -89f, 89f);

        return (yaw, pitch);
    }

    /// <summary>a − b 를 [−180, 180] 로.</summary>
    public static float DeltaAngle(float a, float b)
    {
        float d = (a - b) % 360f;
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        return d;
    }

    /// <summary>
    /// 현재 조준선에서 각도로 가장 가까운 적을 고른다. maxFovDeg 밖은 무시.
    ///
    /// 현재 조준각을 기준으로 고르므로, 한 번 잠긴 표적에 계속 붙어 있는다
    /// (다른 적이 더 가까운 각도로 들어오기 전까지).
    ///
    /// ※ 시야 판정을 하지 않는다. 벽 뒤 적도 겨눈다. 외부 프로세스는 맵
    ///   충돌체를 레이캐스트할 수 없다. 이게 서버 V-LOS 에 잡히는 지점이다.
    /// </summary>
    public static bool TryPickTarget(
        in StatePayloadRaw me, IReadOnlyList<EnemySnapshot> enemies,
        float curYaw, float curPitch, float maxFovDeg, bool aimHead,
        out float yaw, out float pitch)
    {
        yaw = pitch = 0f;

        Vector3 eye = me.Position + new Vector3(0f, EyeOffsetY, 0f);
        float aimY = aimHead ? HeadOffsetY : BodyOffsetY;

        float best = float.MaxValue;
        foreach (var e in enemies)
        {
            Vector3 point = e.State.Position + new Vector3(0f, aimY, 0f);
            var (y, p) = AnglesTo(eye, point);

            float dy = DeltaAngle(y, curYaw);
            float dp = p - curPitch;
            float ang = MathF.Sqrt(dy * dy + dp * dp);

            if (ang > maxFovDeg || ang >= best) continue;
            best = ang;
            yaw = y;
            pitch = p;
        }
        return best < float.MaxValue;
    }
}
