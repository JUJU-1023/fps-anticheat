namespace CheatClient.Core;

/// <summary>
/// 조준각(yaw/pitch) 메모리 조작. 에임핵의 엔진.
///
/// 표적: PlayerController.currentYaw / currentPitch (float, 4바이트 연속)
///   CE 경로: mono-2.0-bdwgc.dll + 0x764290 → [+0x280][+0x1D0][+0xC0][+0x14C]
///   currentPitch = 같은 체인, 마지막 +0x150 (yaw 바로 뒤)
///   - 재시작 검증: pointermap 2회 비교 통과.
///   - 서버 반영 검증: yaw 180 / pitch 45 → combat_events 기록.
///
/// ★ CE 와 ResolveChain 의 base 해석 차이 ★
///   CE 는 (mono베이스 + 0x764290) 을 "한 번 역참조"한 뒤 오프셋을 따라간다.
///   우리 ResolveChain(base, offs) 는 base 를 역참조하지 않고 base+offs[0] 부터
///   역참조한다. 그래서 체인 앞에 0 을 넣어 base 역참조 단계를 맞춘다.
///
///     ResolveChain(monoBase + 0x764290, 0x0, 0x280, 0x1D0, 0xC0, 0x14C)
///       = [[[[[mono+0x764290]+0]+0x280]+0x1D0]+0xC0] + 0x14C   (마지막은 더하기)
///
///   앞의 0x0 이 CE 의 base 역참조에 해당한다.
/// </summary>
public sealed class AimCheat
{
    private const string MonoModule = "mono-2.0-bdwgc.dll";
    private const int BaseOffset = 0x764290;

    // 앞의 0x0 = CE 의 base 역참조. 마지막 0x14C = yaw 필드.
    private static readonly int[] YawChain   = { 0x0, 0x280, 0x1D0, 0xC0, 0x14C };
    private static readonly int[] PitchChain = { 0x0, 0x280, 0x1D0, 0xC0, 0x150 };

    private readonly GameProcess _game;

    public AimCheat(GameProcess game) => _game = game;

    private IntPtr Base => _game.GetModuleBase(MonoModule) + BaseOffset;

    private IntPtr YawAddr   => _game.ResolveChain(Base, YawChain);
    private IntPtr PitchAddr => _game.ResolveChain(Base, PitchChain);

    public float ReadYaw()   => _game.Read<float>(YawAddr);
    public float ReadPitch() => _game.Read<float>(PitchAddr);

    public void WriteAim(float yaw, float pitch)
    {
        _game.Write(YawAddr, yaw);
        _game.Write(PitchAddr, pitch);
    }

    public void WriteYaw(float yaw)     => _game.Write(YawAddr, yaw);
    public void WritePitch(float pitch) => _game.Write(PitchAddr, pitch);

    public bool Probe(out float yaw, out float pitch)
    {
        try { yaw = ReadYaw(); pitch = ReadPitch(); return true; }
        catch { yaw = pitch = 0f; return false; }
    }
}
