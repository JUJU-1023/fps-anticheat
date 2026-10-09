using CheatClient.Native;

namespace CheatClient.Core;

/// <summary>
/// 자동 조준의 "목표 공급자". AimLock 루프가 매 회(약 125Hz) TryGetAim 을 부르고,
/// true 면 그 각도를 쓰고 false 면 아무것도 쓰지 않는다(마우스가 그대로 조작).
///
/// 우클릭을 누르고 있는 동안만 동작한다. 항상 잠기면 시연 중 마우스를 못 쓴다.
/// 키 상태는 GetAsyncKeyState 로 읽기만 한다. 게임에 입력을 넣지 않는다.
/// </summary>
public sealed class AutoAim
{
    private readonly PlayerReader _reader;
    private readonly AimCheat _aim;

    /// <summary>조준선에서 이 각도(도) 안의 적만 잡는다.</summary>
    public volatile float MaxFovDeg = 30f;

    /// <summary>false = 몸통(기본, 히트박스가 커서 안정적) / true = 머리.</summary>
    public volatile bool AimHead = false;

    public volatile bool RequireRightMouse = true;

    public AutoAim(PlayerReader reader, AimCheat aim)
    {
        _reader = reader;
        _aim = aim;
    }

    public bool TryGetAim(out float yaw, out float pitch)
    {
        yaw = pitch = 0f;

        if (RequireRightMouse && (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) == 0)
            return false;

        if (!_reader.TryReadLocal(out var me)) return false;

        var enemies = _reader.ReadLiveEnemies();
        if (enemies.Count == 0) return false;

        if (!_aim.Probe(out float curYaw, out float curPitch)) return false;

        return AimSolver.TryPickTarget(me, enemies, curYaw, curPitch, MaxFovDeg, AimHead, out yaw, out pitch);
    }
}
