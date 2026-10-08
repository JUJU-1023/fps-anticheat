using System.Threading;

namespace CheatClient.Core;

/// <summary>
/// 백그라운드에서 yaw/pitch 를 반복해서 써서 조준을 고정한다.
///
/// 한 번만 쓰면 마우스 입력이 다음 프레임에 currentYaw/Pitch 를 덮으므로,
/// 매 루프마다 다시 써서 잠근다. GatherInput 이 그 값을 input 에 담아
/// 서버로 보낸다. (AimCheat 파일 상단 참조)
///
/// W11 확장: SetTarget 을 고정값 대신 "적 위치로 계산한 각도"로 매 루프
/// 갱신하면 그대로 에임봇이 된다. 지금은 수동 고정만.
/// </summary>
public sealed class AimLock
{
    private const int LoopIntervalMs = 8;   // ~125Hz. 프레임(60Hz)보다 촘촘.

    private readonly AimCheat _aim;
    private readonly Action<string> _log;

    private Thread? _thread;
    private volatile bool _running;
    private volatile float _yaw;
    private volatile float _pitch;

    public AimLock(AimCheat aim, Action<string> log)
    {
        _aim = aim;
        _log = log;
    }

    public void SetTarget(float yaw, float pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        _log($"조준 고정 목표 = yaw {yaw:F1} / pitch {pitch:F1}");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "AimLock" };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(200);
        _thread = null;
        _log("조준 고정 해제");
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                _aim.WriteAim(_yaw, _pitch);
            }
            catch (Exception ex)
            {
                _running = false;
                _log("[고정 중단] " + ex.Message);
                return;
            }
            Thread.Sleep(LoopIntervalMs);
        }
    }
}
