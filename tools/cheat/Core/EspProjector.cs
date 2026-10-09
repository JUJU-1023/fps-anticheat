using System.Numerics;

namespace CheatClient.Core;

/// <summary>
/// 월드 좌표 → 게임 화면 좌표 투영. 카메라 행렬을 메모리에서 찾지 않고
/// 이미 읽고 있는 값(내 위치, 조준각)과 고정 상수(FOV)로 직접 계산한다.
///
/// ─ 카메라 ─
///   위치 = 내 루트 위치 + WeaponConfig.EyeOffset(0, 0.75, 0)
///          (CombatHUD 가 카메라 localPosition 을 EyeOffset 으로 둔다)
///   회전 = Quaternion.Euler(pitch, yaw, 0)   ← AimSolver 와 같은 정의
///   FOV  = 세로 60° (PlayerCharacter.prefab / Arena.unity, 코드에서 안 바뀜)
///   WeaponKick 은 시각 효과만 있고 카메라·currentPitch 를 건드리지 않는다.
///
/// ─ 카메라 기저 (Unity 왼손 좌표계, Ry(yaw)·Rx(pitch)) ─
///   forward = ( sin y·cos p,  −sin p,  cos y·cos p )
///   right   = ( cos y,         0,      −sin y       )
///   up      = ( sin y·sin p,   cos p,   cos y·sin p )
///
/// ─ 투영 ─
///   d = P − cam,  z = d·forward (깊이)
///   ndcX = (d·right) / (z · tan(fov/2) · aspect)
///   ndcY = (d·up)    / (z · tan(fov/2))
///   화면 x = W/2 · (1 + ndcX),  화면 y = H/2 · (1 − ndcY)   (y 는 아래로 증가)
///
///   W, H 는 어떤 단위든 비율만 맞으면 된다. 오버레이는 WPF DIP 단위로 넘긴다.
/// </summary>
public static class EspProjector
{
    public const float VerticalFovDeg = 60f;

    /// <summary>이보다 가까우면(또는 뒤에 있으면) 그리지 않는다.</summary>
    public const float NearClip = 0.3f;

    // 적 루트 위치 기준 높이 오프셋. 서 있으면 루트 y = 바닥 + 1.08 (RestY).
    public const float FeetOffsetY = -1.08f;   // 발바닥
    public const float TopOffsetY  =  0.92f;   // 머리 꼭대기 (머리 히트박스 중심 1.83 + 반지름 여유)

    /// <summary>박스 폭 = 높이 × 이 값. 3인칭 캐릭터 비율에 맞춘 근사.</summary>
    public const float BoxAspect = 0.42f;

    private const float Deg2Rad = MathF.PI / 180f;
    private static readonly float TanHalfFov = MathF.Tan(VerticalFovDeg * 0.5f * Deg2Rad);

    /// <summary>카메라 위치 = 내 루트 + 눈높이.</summary>
    public static Vector3 CameraPosition(in StatePayloadRaw me)
        => me.Position + new Vector3(0f, AimSolver.EyeOffsetY, 0f);

    /// <summary>
    /// world 를 화면 좌표로 투영한다. 카메라 뒤(또는 NearClip 이내)면 false.
    /// 화면 밖이어도 앞에 있으면 true 를 돌려준다(좌표가 0..W 범위를 벗어남).
    /// </summary>
    public static bool TryProject(
        Vector3 cam, float yawDeg, float pitchDeg, Vector3 world,
        double viewW, double viewH,
        out double screenX, out double screenY, out float depth)
    {
        screenX = screenY = 0;

        float y = yawDeg * Deg2Rad, p = pitchDeg * Deg2Rad;
        float sy = MathF.Sin(y), cy = MathF.Cos(y);
        float sp = MathF.Sin(p), cp = MathF.Cos(p);

        var forward = new Vector3(sy * cp, -sp, cy * cp);
        var right   = new Vector3(cy, 0f, -sy);
        var up      = new Vector3(sy * sp, cp, cy * sp);

        Vector3 d = world - cam;
        depth = Vector3.Dot(d, forward);
        if (depth < NearClip) return false;

        double aspect = viewW / viewH;
        double ndcX = Vector3.Dot(d, right) / (depth * TanHalfFov * aspect);
        double ndcY = Vector3.Dot(d, up)    / (depth * TanHalfFov);

        screenX = viewW * 0.5 * (1.0 + ndcX);
        screenY = viewH * 0.5 * (1.0 - ndcY);
        return true;
    }
}
