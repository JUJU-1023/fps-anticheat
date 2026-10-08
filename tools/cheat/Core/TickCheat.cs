using System.Diagnostics;
using System.Runtime.InteropServices;
using CheatClient.Native;

namespace CheatClient.Core;

/// <summary>
/// CurrentTick 메모리 조작. 스피드핵·연사핵의 공통 엔진.
///
/// 표적: mono-2.0-bdwgc.dll + 0x73E378 (NetworkTickSystem.CurrentTick, int)
///   - 포인터 체인 아님. 모듈 베이스 + 단일 오프셋.
///   - 재시작 검증 통과 (docs/cheat_offsets.md 참조).
///
/// ★ 틱을 절대값으로 덮어쓰지 않는다. 게임이 매 FixedUpdate 마다
///   CurrentTick++ 를 하므로, 현재값을 읽어 delta 를 더한다.
/// </summary>
public sealed class TickCheat
{
    private const int TICK_OFFSET = 0x73E378;
    private const string MonoModule = "mono-2.0-bdwgc.dll";

    private readonly GameProcess _game;
    private IntPtr _tickAddr = IntPtr.Zero;

    /// <summary>진단 로그 콜백(선택). 설정하면 PushTick 이 쓰기 전/후 값을 보고한다.</summary>
    public Action<string>? Diag { get; set; }

    private bool _diagDone;   // 쓰기 검증 로그는 1회만

    public TickCheat(GameProcess game) => _game = game;

    public bool Resolved => _tickAddr != IntPtr.Zero;

    /// <summary>모듈 베이스 + 오프셋으로 틱 주소를 고정한다. attach 직후 1회.</summary>
    public void Resolve()
    {
        IntPtr baseAddr = _game.GetModuleBase(MonoModule);
        _tickAddr = baseAddr + TICK_OFFSET;
        Diag?.Invoke($"[진단] 모듈베이스=0x{baseAddr.ToInt64():X} " +
                     $"틱주소=0x{_tickAddr.ToInt64():X} (CE의 최종주소와 같아야 함)");
    }

    public int ReadTick()
    {
        EnsureResolved();
        return _game.Read<int>(_tickAddr);
    }

    /// <summary>현재 틱 + delta. 첫 호출 때 쓰기가 실제로 먹는지 검증 로그를 남긴다.</summary>
    public void PushTick(int delta)
    {
        EnsureResolved();
        int before = _game.Read<int>(_tickAddr);
        _game.Write(_tickAddr, before + delta);

        if (!_diagDone)
        {
            _diagDone = true;
            int after = _game.Read<int>(_tickAddr);
            // after 가 before+delta 근처면 쓰기 성공(게임 ++ 때문에 몇 틱 더 클 수 있음).
            // after 가 before 근처(delta 반영 안 됨)면 쓰기 실패 — 권한/주소 의심.
            Diag?.Invoke($"[진단] 쓰기전={before} +{delta} → 다시읽음={after} " +
                         $"(델타반영 {(after >= before + delta - 5 ? "O 성공" : "X 실패")})");
        }
    }

    private void EnsureResolved()
    {
        if (!Resolved)
            throw new InvalidOperationException("Resolve() 를 먼저 호출하라.");
    }
}
