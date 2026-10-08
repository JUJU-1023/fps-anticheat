using System.Threading;

namespace CheatClient.Core;

/// <summary>
/// 백그라운드 스레드에서 틱을 주기적으로 밀어 치트를 구현한다.
///
/// 임계값 근거 (게임 상수)
///   TickRate            = 60   (FixedUpdate/s, 초당 입력 수)
///   FireIntervalTicks   = 6    (발사 게이트: input.tick 간격 >= 6)
///   MaxTickJump         = 120  (V-MOVE: 틱 점프 이보다 크면 TickAhead)
///
/// ★ W10 수정: delta 를 키웠다.
///   기존 +5(100Hz)는 게임의 CurrentTick++ 와 타이밍 경합에서 밀려
///   체감이 없었다(진단: 쓰기는 성공하나 다음 프레임에 게임이 되돌림).
///   발사 게이트는 "input.tick 이 _lastFireTick 보다 6 이상 앞"이면 통과하므로,
///   매 루프마다 현재값 + 큰 수로 확실히 앞세운다.
///
///   RapidFire : 프레임당 실질 증가가 6~119 가 되도록 크게 민다.
///               발사 게이트만 통과 -> V-FIRE-01. V-MOVE 는 안 건드림.
///   TickForge : 프레임당 증가 >= 120 -> V-MOVE-01 TickAhead.
/// </summary>
public sealed class CheatLoop
{
    public enum Mode { Off, RapidFire, TickForge }

    // 루프 주기. 짧을수록 게임 ++ 를 확실히 앞선다.
    private const int LoopIntervalMs = 5;   // 200Hz

    // RapidFire: 매 루프 +40. 200Hz × 40 = +8000/s ≈ 프레임당 133... 은 120 초과라
    //   아래 Push 에서 프레임당 증가를 노린다. 실측 보며 조정하도록 상수로 노출.
    //   우선 발사 게이트(>=6)를 확실히 넘기되 V-MOVE(>=120)는 아슬아슬 피하는 값.
    private const int RapidDelta = 10;      // 200Hz × 10 = +2000/s ≈ 프레임당 33틱

    // TickForge: 매 루프 +400. 200Hz × 400 = +80000/s ≈ 프레임당 1333틱 >> 120.
    private const int ForgeDelta = 400;

    private readonly TickCheat _tick;
    private readonly Action<string> _log;

    private Thread? _thread;
    private volatile bool _running;
    private volatile Mode _mode = Mode.Off;

    public CheatLoop(TickCheat tick, Action<string> log)
    {
        _tick = tick;
        _log = log;
    }

    public Mode Current => _mode;

    public void SetMode(Mode mode)
    {
        _mode = mode;
        _log($"모드 = {mode}");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CheatLoop" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(200);
        _thread = null;
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                switch (_mode)
                {
                    case Mode.RapidFire: _tick.PushTick(RapidDelta); break;
                    case Mode.TickForge: _tick.PushTick(ForgeDelta); break;
                    case Mode.Off: break;
                }
            }
            catch (Exception ex)
            {
                _mode = Mode.Off;
                _running = false;
                _log("[루프 중단] " + ex.Message);
                return;
            }

            Thread.Sleep(LoopIntervalMs);
        }
    }
}
