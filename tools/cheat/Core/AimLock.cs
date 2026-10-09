using System.IO;
using System.Threading;

namespace CheatClient.Core;

/// <summary>매 루프 조준 목표를 돌려준다. false 면 이번 회에는 쓰지 않는다.</summary>
public delegate bool AimProvider(out float yaw, out float pitch);

/// <summary>
/// 백그라운드에서 yaw/pitch 를 반복해서 써서 조준을 고정한다.
///
/// 한 번만 쓰면 마우스 입력이 다음 프레임에 currentYaw/Pitch 를 덮으므로,
/// 매 루프마다 다시 써서 잠근다. GatherInput 이 그 값을 input 에 담아
/// 서버로 보낸다. (AimCheat 파일 상단 참조)
///
/// 두 가지 모드
///   수동 고정  SetProvider(null) + SetTarget(yaw, pitch)   — W10
///   자동 조준  SetProvider(autoAim.TryGetAim)              — W11
///
/// 자동 조준에서 SetTarget 을 쓰지 않는 이유: SetTarget 은 호출마다 로그를
/// 남겨서 125Hz 로 부르면 로그창이 넘친다.
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
    private volatile AimProvider? _provider;

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

    /// <summary>null 이면 수동 고정, 아니면 자동 조준.</summary>
    public void SetProvider(AimProvider? provider)
    {
        _provider = provider;
        _log(provider == null ? "모드: 수동 고정" : "모드: 자동 조준 (우클릭 누르는 동안)");
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
            var provider = _provider;
            try
            {
                if (provider == null)
                    _aim.WriteAim(_yaw, _pitch);
                else if (provider(out float y, out float p))
                    _aim.WriteAim(y, p);
            }
            catch (IOException) when (provider != null)
            {
                // 자동 모드: 사망·리스폰 순간의 일시적인 체인 끊김은 넘긴다.
                Thread.Sleep(50);
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
