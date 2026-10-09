using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace CheatClient.Core;

/// <summary>
/// 게임의 StatePayload 와 같은 메모리 배치. 0x24(36) 바이트, 패딩 없음.
///   +0x00 tick  +0x04 position  +0x10 velocity  +0x1C yaw  +0x20 pitch
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct StatePayloadRaw
{
    public int     Tick;
    public Vector3 Position;
    public Vector3 Velocity;
    public float   Yaw;
    public float   Pitch;
}

/// <summary>살아 있는 적 하나의 화면 기준 상태 (보간기가 그리는 latestTick-6 슬롯).</summary>
public readonly record struct EnemySnapshot(IntPtr Rpi, StatePayloadRaw State);

/// <summary>
/// W11 CE 실측으로 확정한 Mono 필드 오프셋. 프리즈 빌드(해시 검사 통과)에서만 유효.
/// docs/cheat_offsets.md 와 같아야 한다.
/// </summary>
internal static class MonoOffsets
{
    // PlayerController
    public const int PC_StateBuffer   = 0xB8;
    public const int PC_Interpolator  = 0xC0;

    // RemotePlayerInterpolator
    public const int RPI_StateBuffer  = 0x28;
    public const int RPI_LatestTick   = 0x48;

    // CircularBuffer<T>
    public const int CB_Array         = 0x10;
    public const int CB_Size          = 0x18;

    // Mono 배열 (64비트): 길이 +0x18, 데이터 +0x20
    public const int Arr_Length       = 0x18;
    public const int Arr_Data         = 0x20;

    public const int ElemSize         = 0x24;   // sizeof(StatePayload)
    public const int BufferSize       = 1024;

    /// <summary>보간기 지연 100ms ÷ 16.67ms. 화면과 서버 랙 보상이 보는 시점.</summary>
    public const int InterpDelayTicks = 6;
}

internal sealed class EnemyTrack
{
    public EnemyTrack(long rpi) => Rpi = rpi;

    public readonly long Rpi;
    public bool Seen;
    public int  LastLatestTick;
    public long LastChangeMs;
}

/// <summary>
/// 내 위치와 적 위치를 메모리에서 읽는다. 순수 RPM, 쓰기 없음.
///
/// ─ 내 위치 ─
///   PlayerController.stateBuffer 에 매 틱 예측 상태가 쌓인다.
///   1024칸(36KB)을 한 번에 읽어 tick 이 가장 큰 원소를 쓴다.
///   CurrentTick 주소를 믿지 않아도 된다. (W10 의 0x73E378 은 다른 카운터였다)
///
/// ─ 적 위치 ─
///   RemotePlayerInterpolator.stateBuffer 의 (latestTick - 6) 슬롯.
///   보간기가 화면에 그리는 시점이고, 서버 랙 보상이 되감는 시점과 같다.
///   최신 슬롯을 겨누면 100ms 앞을 쏘게 되어 움직이는 적에게 빗나간다.
///
///   슬롯의 tick 이 기대값과 다르면 버린다. 보간기 자신도 같은 검증을 한다.
///
///   ★ 적 버퍼는 캐시하지 않는다 ★ ClearBuffer() 가 스폰·리스폰마다
///   new CircularBuffer 를 만들어 주소가 바뀐다. 매번 RPI 부터 다시 따라간다.
///
/// ─ 적 보간기 찾기 ─
///   NGO SpawnManager 를 순회하지 않는다. CE 에서 한 것을 그대로 한다.
///     1. 내 보간기의 vtable(객체 첫 8바이트)을 체인으로 읽는다.
///     2. 쓰기 가능 메모리에서 그 값을 스캔한다.
///     3. 버퍼 구조(size 1024, 배열 길이 1024)로 걸러낸다.
///   살아 있는지는 latestTick 이 계속 오르는지로 판단한다. 사망 중인 적은
///   입력을 안 보내서 latestTick 이 멈추므로 자연스럽게 빠진다.
/// </summary>
public sealed class PlayerReader : IDisposable
{
    private const string MonoModule = "mono-2.0-bdwgc.dll";
    private const int BaseOffset = 0x764290;

    // AimCheat.YawChain 의 마지막 0x14C 를 0 으로 바꾼 것 = PlayerController 객체 주소.
    private static readonly int[] PcChain = { 0x0, 0x280, 0x1D0, 0xC0, 0x0 };

    private const int  ScanIntervalMs = 5000;
    private const int  ScanChunkBytes = 4 * 1024 * 1024;
    private const long StaleAfterMs   = 1000;

    private readonly GameProcess _game;
    private readonly Action<string> _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly byte[] _localBuf = new byte[MonoOffsets.BufferSize * MonoOffsets.ElemSize];
    private readonly object _localLock = new();

    private volatile EnemyTrack[] _tracks = Array.Empty<EnemyTrack>();
    private Thread? _scanThread;
    private volatile bool _running;
    private int _lastLoggedCount = -1;

    public PlayerReader(GameProcess game, Action<string> log)
    {
        _game = game;
        _log = log;

        if (Unsafe.SizeOf<StatePayloadRaw>() != MonoOffsets.ElemSize)
            throw new InvalidOperationException(
                $"StatePayloadRaw 크기 {Unsafe.SizeOf<StatePayloadRaw>()} ≠ 0x24. 구조 정의 확인.");
    }

    /// <summary>스캔으로 찾은 보간기 수 (살아 있지 않은 것 포함).</summary>
    public int TrackedCount => _tracks.Length;

    // -----------------------------------------------------------------
    //  내 위치
    // -----------------------------------------------------------------

    private IntPtr PlayerControllerObject()
        => _game.ResolveChain(_game.GetModuleBase(MonoModule) + BaseOffset, PcChain);

    /// <summary>
    /// 내 최신 예측 상태. position 은 루트 Transform 위치(서 있으면 y = 1.08).
    ///
    /// ※ 게임을 끄지 않고 재접속하면 이전 세션의 큰 tick 이 남아 있을 수 있다.
    ///   테스트는 게임을 새로 켠 상태에서 한다.
    /// </summary>
    public bool TryReadLocal(out StatePayloadRaw state)
    {
        state = default;
        try
        {
            IntPtr pc = PlayerControllerObject();
            if (!_game.TryRead(pc + MonoOffsets.PC_StateBuffer, out long cb) || cb == 0) return false;
            if (!_game.TryRead(new IntPtr(cb + MonoOffsets.CB_Array), out long arr) || arr == 0) return false;

            lock (_localLock)
            {
                if (!_game.TryReadBytes(new IntPtr(arr + MonoOffsets.Arr_Data), _localBuf, _localBuf.Length))
                    return false;

                int bestIdx = -1, bestTick = 0;
                for (int i = 0; i < MonoOffsets.BufferSize; i++)
                {
                    int t = BitConverter.ToInt32(_localBuf, i * MonoOffsets.ElemSize);
                    if (t > bestTick) { bestTick = t; bestIdx = i; }
                }
                if (bestIdx < 0) return false;

                state = MemoryMarshal.Read<StatePayloadRaw>(
                    _localBuf.AsSpan(bestIdx * MonoOffsets.ElemSize, MonoOffsets.ElemSize));
                return true;
            }
        }
        catch (IOException)
        {
            return false;   // 체인 끊김 (로비, 씬 전환 등)
        }
    }

    // -----------------------------------------------------------------
    //  적 위치
    // -----------------------------------------------------------------

    /// <summary>살아 있는 적들의 화면 기준 상태. 호출할 때마다 새로 읽는다.</summary>
    public List<EnemySnapshot> ReadLiveEnemies()
    {
        var result = new List<EnemySnapshot>();
        long now = _clock.ElapsedMilliseconds;

        foreach (var t in _tracks)
        {
            var rpi = new IntPtr(t.Rpi);
            if (!_game.TryRead(rpi + MonoOffsets.RPI_LatestTick, out int lt)) continue;

            bool live;
            lock (t)
            {
                if (!t.Seen)
                {
                    // 처음 본 값은 기준만 세운다. 한 번이라도 변해야 살아 있는 것으로 본다.
                    // 이게 없으면 예전 스폰에서 남은 객체가 스캔 직후 1초간 적으로 잡힌다.
                    t.Seen = true;
                    t.LastLatestTick = lt;
                    t.LastChangeMs = long.MinValue / 2;
                }
                else if (lt != t.LastLatestTick)
                {
                    t.LastLatestTick = lt;
                    t.LastChangeMs = now;
                }
                live = now - t.LastChangeMs <= StaleAfterMs;
            }

            if (!live || lt < MonoOffsets.InterpDelayTicks) continue;

            if (TryReadSlot(rpi, lt - MonoOffsets.InterpDelayTicks, out var s))
                result.Add(new EnemySnapshot(rpi, s));
        }
        return result;
    }

    private bool TryReadSlot(IntPtr rpi, int tick, out StatePayloadRaw s)
    {
        s = default;
        if (!_game.TryRead(rpi + MonoOffsets.RPI_StateBuffer, out long cb) || cb == 0) return false;
        if (!_game.TryRead(new IntPtr(cb + MonoOffsets.CB_Array), out long arr) || arr == 0) return false;

        long addr = arr + MonoOffsets.Arr_Data + (long)(tick % MonoOffsets.BufferSize) * MonoOffsets.ElemSize;
        if (!_game.TryRead(new IntPtr(addr), out s)) return false;

        return s.Tick == tick;   // 빈 슬롯·옛 값이면 버린다
    }

    // -----------------------------------------------------------------
    //  적 보간기 스캔 (백그라운드)
    // -----------------------------------------------------------------

    public void StartScanning()
    {
        if (_running) return;
        _running = true;
        _scanThread = new Thread(ScanLoop) { IsBackground = true, Name = "InterpScan" };
        _scanThread.Start();
    }

    private void ScanLoop()
    {
        while (_running)
        {
            try { ScanOnce(); }
            catch (Exception ex) { _log("[스캔 오류] " + ex.Message); }

            for (int i = 0; i < ScanIntervalMs / 100 && _running; i++)
                Thread.Sleep(100);
        }
    }

    private bool TryGetMyInterpolator(out long myRpi, out long vtable)
    {
        myRpi = vtable = 0;
        try
        {
            IntPtr pc = PlayerControllerObject();
            if (!_game.TryRead(pc + MonoOffsets.PC_Interpolator, out myRpi) || myRpi == 0) return false;
            return _game.TryRead(new IntPtr(myRpi), out vtable) && vtable != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void ScanOnce()
    {
        if (!TryGetMyInterpolator(out long mine, out long vtable))
        {
            if (_lastLoggedCount != -2) _log("[스캔] 내 보간기를 읽지 못함 (매치 입장 전?)");
            _lastLoggedCount = -2;
            return;
        }

        var sw = Stopwatch.StartNew();
        var found = new List<long>();
        var buf = new byte[ScanChunkBytes];
        long scanned = 0;

        foreach (var (regionBase, regionSize) in _game.EnumerateWritableRegions())
        {
            if (!_running) return;

            long start = regionBase.ToInt64();
            long end = start + regionSize;

            for (long p = start; p < end; p += ScanChunkBytes)
            {
                int len = (int)Math.Min(ScanChunkBytes, end - p);
                if (!_game.TryReadBytes(new IntPtr(p), buf, len)) continue;
                scanned += len;

                // 영역 시작은 페이지 정렬이라 8바이트 단위로 보면 된다. (객체는 8 정렬)
                var words = MemoryMarshal.Cast<byte, long>(buf.AsSpan(0, len & ~7));
                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i] != vtable) continue;
                    long cand = p + (long)i * 8;
                    if (cand != mine && LooksLikeInterpolator(cand)) found.Add(cand);
                }
            }
        }

        // 기존 추적 객체는 살려 둔다. 생존 판정 기록(LastChangeMs)을 이어가야 한다.
        var old = _tracks.ToDictionary(t => t.Rpi);
        _tracks = found
            .Select(r => old.TryGetValue(r, out var t) ? t : new EnemyTrack(r))
            .ToArray();

        if (_tracks.Length != _lastLoggedCount)
        {
            _log($"[스캔] 보간기 {_tracks.Length}개 · {sw.ElapsedMilliseconds}ms · {scanned >> 20}MB");
            _lastLoggedCount = _tracks.Length;
        }
    }

    /// <summary>
    /// vtable 이 같은 8바이트가 진짜 보간기 객체인지 버퍼 구조로 확인한다.
    /// CE 에서 걸러냈던 쓰레기 후보(-11억 등)가 여기서 떨어진다.
    /// </summary>
    private bool LooksLikeInterpolator(long cand)
    {
        if (!_game.TryRead(new IntPtr(cand + MonoOffsets.RPI_StateBuffer), out long cb) || cb == 0) return false;
        if (!_game.TryRead(new IntPtr(cb + MonoOffsets.CB_Size), out int size) || size != MonoOffsets.BufferSize) return false;
        if (!_game.TryRead(new IntPtr(cb + MonoOffsets.CB_Array), out long arr) || arr == 0) return false;
        if (!_game.TryRead(new IntPtr(arr + MonoOffsets.Arr_Length), out int len) || len != MonoOffsets.BufferSize) return false;
        return _game.TryRead(new IntPtr(cand + MonoOffsets.RPI_LatestTick), out int lt) && lt >= -1;
    }

    public void Dispose()
    {
        _running = false;
        _scanThread?.Join(500);
        _scanThread = null;
    }
}
