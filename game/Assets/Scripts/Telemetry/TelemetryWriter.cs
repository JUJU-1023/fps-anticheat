// =====================================================================
//  TelemetryWriter.cs
//  경로: game/Assets/Scripts/Telemetry/TelemetryWriter.cs
//
//  텔레메트리를 JSONL 파일로 내보낸다. DB에는 접속하지 않는다.
//  파일을 읽어 MariaDB에 넣는 일은 별도 ingester(Python)가 담당한다.
//
//  설계
//   - 게임 스레드는 완성된 JSON 문자열을 큐에 넣기만 한다.
//   - 백그라운드 스레드가 1초마다 파일에 flush 한다.
//     디스크가 멈춰도 서버 틱이 밀리지 않는다.
//   - DB가 죽어 있어도 이 파일은 계속 쌓인다. 파일 자체가 백로그다.
//
//  씬 배치: 빈 GameObject 하나에 이 컴포넌트만 붙이면 된다.
//           NetworkManager.OnServerStarted 를 직접 구독하므로
//           Bootstrap.cs 수정이 필요 없다.
// =====================================================================

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>JSON 직렬화 보조. 로케일 무관하게 항상 InvariantCulture 를 쓴다.</summary>
public static class TJson
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>문자열을 JSON 문자열 리터럴로 변환한다.</summary>
    public static string Str(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";

        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// float 를 JSON 숫자로. 한국어 로케일에서 소수점이 쉼표로 나오면
    /// JSON 이 깨지므로 반드시 InvariantCulture 를 쓴다.
    /// </summary>
    public static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
        return v.ToString("0.#####", Inv);
    }

    public static string Iso(DateTime utc)
        => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Inv);

    public static string Now() => Iso(DateTime.UtcNow);
}


public class TelemetryWriter : MonoBehaviour
{
    public static TelemetryWriter Instance { get; private set; }

    [Header("출력")]
    [Tooltip("비우면 커맨드라인 -telemetryDir, 그것도 없으면 <작업디렉터리>/telemetry")]
    [SerializeField] private string outputDir = "";

    [Tooltip("파일 flush 주기(초). ingester 지연에 직접 영향.")]
    [SerializeField] private float flushIntervalSec = 1f;

    [Tooltip("큐 상한. 초과분은 드롭하고 경고를 남긴다.")]
    [SerializeField] private int maxQueued = 100000;

    /// <summary>이번 세션의 GUID. ingester 가 matches 행으로 변환한다.</summary>
    public string MatchUid { get; private set; }

    public bool IsActive => _running;
    public string CurrentPath => _path;

    private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
    private StreamWriter _writer;
    private Thread _thread;
    private volatile bool _running;
    private int _dropped;
    private string _path;

    // -----------------------------------------------------------------
    //  수명 주기
    // -----------------------------------------------------------------

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[Telemetry] NetworkManager 가 없다. 텔레메트리 비활성.");
            return;
        }

        // 이미 서버가 떠 있으면 즉시 시작, 아니면 시작 시점에 시작.
        if (nm.IsServer) BeginMatch();
        else nm.OnServerStarted += BeginMatch;
    }

    private void OnDestroy()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null) nm.OnServerStarted -= BeginMatch;
        EndMatch();
        if (Instance == this) Instance = null;
    }

    private void OnApplicationQuit() => EndMatch();

    // -----------------------------------------------------------------
    //  세션 시작/종료
    // -----------------------------------------------------------------

    public void BeginMatch()
    {
        if (_running) return;

        string dir = ResolveDir();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Telemetry] 출력 디렉터리 생성 실패 ({dir}): {e.Message}");
            return;
        }

        var startedAt = DateTime.UtcNow;
        MatchUid = Guid.NewGuid().ToString();

        string fileName = $"{startedAt.ToString("yyyyMMdd-HHmmss", TJson.Inv)}_{MatchUid.Substring(0, 8)}.jsonl";
        _path = Path.Combine(dir, fileName);

        try
        {
            var fs = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(fs, new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Debug.LogError($"[Telemetry] 파일 열기 실패 ({_path}): {e.Message}");
            return;
        }

        _running = true;
        _thread = new Thread(FlushLoop) { IsBackground = true, Name = "TelemetryFlush" };
        _thread.Start();

        var sb = new StringBuilder(256);
        sb.Append("{\"t\":\"match_start\"");
        sb.Append(",\"match_uid\":").Append(TJson.Str(MatchUid));
        sb.Append(",\"started_at\":").Append(TJson.Str(TJson.Iso(startedAt)));
        sb.Append(",\"map\":").Append(TJson.Str(SceneManager.GetActiveScene().name));
        sb.Append(",\"tick_rate\":").Append(Mathf.RoundToInt(1f / Time.fixedDeltaTime).ToString(TJson.Inv));
        sb.Append(",\"label\":\"unknown\"");   // W11 하네스에서 clean/cheat 로 덮어쓴다
        sb.Append('}');
        Write(sb.ToString());

        Debug.Log($"[Telemetry] 세션 시작 match={MatchUid} path={_path}");
    }

    public void EndMatch()
    {
        if (!_running) return;

        var sb = new StringBuilder(128);
        sb.Append("{\"t\":\"match_end\"");
        sb.Append(",\"match_uid\":").Append(TJson.Str(MatchUid));
        sb.Append(",\"ended_at\":").Append(TJson.Str(TJson.Now()));
        sb.Append('}');
        Write(sb.ToString());

        _running = false;                 // flush 루프 종료 신호
        try { _thread?.Join(3000); } catch { /* ignore */ }

        try
        {
            Drain();                      // 남은 줄 마저 기록
            _writer?.Flush();
        }
        catch (Exception e)
        {
            Debug.LogError($"[Telemetry] 종료 flush 실패: {e.Message}");
        }
        finally
        {
            _writer?.Dispose();
            _writer = null;
        }

        Debug.Log($"[Telemetry] 세션 종료 match={MatchUid}");
    }

    // -----------------------------------------------------------------
    //  쓰기
    // -----------------------------------------------------------------

    /// <summary>완성된 JSON 한 줄을 큐에 넣는다. 게임 스레드에서 호출해도 안전하다.</summary>
    public void Write(string jsonLine)
    {
        if (!_running && _writer == null) return;

        if (_queue.Count >= maxQueued)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(jsonLine);
    }

    private void FlushLoop()
    {
        int intervalMs = Math.Max(100, (int)(flushIntervalSec * 1000f));
        while (_running)
        {
            Thread.Sleep(intervalMs);
            try
            {
                Drain();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Telemetry] flush 실패: {e.Message}");
            }
        }
    }

    private void Drain()
    {
        if (_writer == null) return;

        int n = 0;
        while (_queue.TryDequeue(out var line))
        {
            _writer.WriteLine(line);
            n++;
        }

        // 매번 flush 해야 ingester 가 완성된 줄만 읽는다.
        if (n > 0) _writer.Flush();

        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            Debug.LogWarning($"[Telemetry] 큐 포화로 {dropped}줄 드롭");
    }

    // -----------------------------------------------------------------
    //  경로 결정
    // -----------------------------------------------------------------

    private string ResolveDir()
    {
        if (!string.IsNullOrEmpty(outputDir)) return outputDir;

        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-telemetryDir")
                return args[i + 1];

        return Path.Combine(Directory.GetCurrentDirectory(), "telemetry");
    }
}
