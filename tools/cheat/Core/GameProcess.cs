using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CheatClient.Native;

namespace CheatClient.Core;

/// <summary>
/// 대상 게임 프로세스에 붙어 메모리를 읽고 쓴다. 순수 외부 방식(RPM/WPM).
/// attach 전에 설치 폴더 파일 해시를 프리즈 빌드 지문과 대조한다.
///
/// W11 추가
///   - GetModuleBase 캐시. 기존에는 호출마다 _proc.Modules 를 열거했는데,
///     AimLock 이 125Hz 로 체인을 해석하면서 매번 모듈 목록을 다시 만들고 있었다.
///   - TryRead / TryReadBytes. 스캔 후보 검증처럼 실패가 흔한 경로에서
///     예외를 던지지 않는다.
///   - EnumerateWritableRegions. 적 보간기 vtable 스캔용.
/// </summary>
public sealed class GameProcess : IDisposable
{
    public const string ProcessName = "Game";   // Game.exe

    // docs/cheat_offsets.md 와 반드시 일치. 하나라도 다르면 체인이 무효이므로 거부.
    private static readonly (string rel, string sha)[] ExpectedHashes =
    {
        ("Game_Data/Managed/Assembly-CSharp.dll",
         "e579acf9c596d28f7df719e5340fb5efa11231edc698bcd6b2c9e772b6ae0a86"),
        ("UnityPlayer.dll",
         "b3da7eb4e7429f03123f9053079f37d90a302890e8a3e4b4f8f0bb494c0f2335"),
        ("MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll",
         "be655f08bd65d0b8e83bf29c47ce780f060a43f37e21e800f6c2937466c0c3a4"),
    };

    private Process? _proc;
    private IntPtr _handle = IntPtr.Zero;

    private readonly Dictionary<string, IntPtr> _moduleCache =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsAttached => _handle != IntPtr.Zero;
    public int Pid => _proc?.Id ?? -1;

    /// <summary>게임을 찾아 해시를 검증하고 attach 한다.</summary>
    public void Attach()
    {
        if (IsAttached) return;

        var procs = Process.GetProcessesByName(ProcessName);
        if (procs.Length == 0)
            throw new InvalidOperationException($"'{ProcessName}.exe' 를 찾을 수 없다. 게임을 먼저 실행하라.");
        if (procs.Length > 1)
            throw new InvalidOperationException($"'{ProcessName}.exe' 가 {procs.Length}개 떠 있다. 하나만 남겨라.");

        var proc = procs[0];

        string? exePath = proc.MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
            throw new InvalidOperationException("프로세스 경로를 읽을 수 없다. 관리자 권한으로 실행했는가?");

        VerifyHashes(Path.GetDirectoryName(exePath)!);

        const Win32.ProcessAccess access =
            Win32.ProcessAccess.VmOperation | Win32.ProcessAccess.VmRead |
            Win32.ProcessAccess.VmWrite | Win32.ProcessAccess.QueryInformation;

        IntPtr h = Win32.OpenProcess(access, false, proc.Id);
        if (h == IntPtr.Zero)
            throw new InvalidOperationException(
                $"OpenProcess 실패 (Win32 {Marshal.GetLastWin32Error()}). 관리자 권한으로 실행하라.");

        _proc = proc;
        _handle = h;
    }

    /// <summary>설치 폴더의 세 파일 SHA-256 을 상수와 대조한다.</summary>
    private static void VerifyHashes(string dir)
    {
        foreach (var (rel, expected) in ExpectedHashes)
        {
            string path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new InvalidOperationException($"대상 파일 없음: {rel}");

            string actual = Sha256(path);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"빌드 불일치: {rel}\n기대 {expected[..16]}…\n실제 {actual[..16]}…\n" +
                    "클라가 재빌드됐다. 포인터 체인이 무효이므로 중단한다.");
        }
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// 모듈 베이스 주소. 네이티브 포인터 체인의 기준점.
    /// 프로세스가 살아 있는 동안 베이스는 바뀌지 않으므로 처음 한 번만 찾는다.
    /// </summary>
    public IntPtr GetModuleBase(string moduleName)
    {
        if (_proc == null) throw new InvalidOperationException("attach 되지 않음.");

        lock (_moduleCache)
        {
            if (_moduleCache.TryGetValue(moduleName, out var cached)) return cached;

            foreach (ProcessModule m in _proc.Modules)
            {
                if (string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    _moduleCache[moduleName] = m.BaseAddress;
                    return m.BaseAddress;
                }
            }
        }
        throw new InvalidOperationException($"모듈을 찾을 수 없음: {moduleName}");
    }

    // --- 메모리 읽기/쓰기 ---

    public T Read<T>(IntPtr address) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        byte[] buf = new byte[size];
        if (!Win32.ReadProcessMemory(_handle, address, buf, size, out var read) || (int)read != size)
            throw new IOException($"RPM 실패 @0x{address.ToInt64():X} (Win32 {Marshal.GetLastWin32Error()})");

        var gc = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(gc.AddrOfPinnedObject()); }
        finally { gc.Free(); }
    }

    /// <summary>
    /// 실패해도 예외를 던지지 않는 읽기. 스캔 후보 검증처럼 실패가 정상인
    /// 경로에서 쓴다. 예외는 비싸서 수천 번 던지면 스캔이 눈에 띄게 느려진다.
    /// </summary>
    public bool TryRead<T>(IntPtr address, out T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        byte[] buf = new byte[size];
        if (Win32.ReadProcessMemory(_handle, address, buf, size, out var read) && (int)read == size)
        {
            value = MemoryMarshal.Read<T>(buf);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>size 바이트를 buffer 앞부분에 읽는다. buffer 는 size 이상이어야 한다.</summary>
    public bool TryReadBytes(IntPtr address, byte[] buffer, int size)
        => Win32.ReadProcessMemory(_handle, address, buffer, size, out var read) && (int)read == size;

    public void Write<T>(IntPtr address, T value) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        byte[] buf = new byte[size];
        var gc = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(value, gc.AddrOfPinnedObject(), false); }
        finally { gc.Free(); }

        if (!Win32.WriteProcessMemory(_handle, address, buf, size, out var written) || (int)written != size)
            throw new IOException($"WPM 실패 @0x{address.ToInt64():X} (Win32 {Marshal.GetLastWin32Error()})");
    }

    /// <summary>
    /// 다중 레벨 포인터 체인 해석. base+off0 포인터를 따라가고, 마지막 오프셋은
    /// 역참조하지 않고 더한다 (Cheat Engine 규약). 값이 있는 최종 주소를 반환한다.
    ///
    /// ※ CE 와 달리 base 자체는 역참조하지 않는다. CE 체인을 옮길 때는
    ///   앞에 0x0 을 하나 붙인다. (AimCheat 상단 주석 참조)
    /// </summary>
    public IntPtr ResolveChain(IntPtr baseAddress, params int[] offsets)
    {
        if (offsets.Length == 0) return baseAddress;
        long addr = baseAddress.ToInt64();
        for (int i = 0; i < offsets.Length - 1; i++)
        {
            addr = Read<long>(new IntPtr(addr + offsets[i]));
            if (addr == 0) throw new IOException($"체인 끊김: 레벨 {i} 에서 null");
        }
        return new IntPtr(addr + offsets[^1]);
    }

    /// <summary>
    /// 커밋된 private 읽기/쓰기 영역을 열거한다. Mono GC 힙이 여기에 있다.
    ///
    /// 실행 영역, 매핑 파일, 가드 페이지는 건너뛴다. 객체가 있을 수 없는
    /// 곳이고, 가드 페이지는 읽으면 스택 확장이 꼬일 수 있다.
    /// </summary>
    public IEnumerable<(IntPtr Base, long Size)> EnumerateWritableRegions()
    {
        if (_handle == IntPtr.Zero) yield break;

        long addr = 0x10000;
        const long maxUserAddr = 0x7FFF_FFFF_0000;
        var mbiSize = new IntPtr(Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>());

        while (addr < maxUserAddr)
        {
            if (Win32.VirtualQueryEx(_handle, new IntPtr(addr), out var mbi, mbiSize) == IntPtr.Zero)
                yield break;

            long regionBase = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();

            bool usable =
                mbi.State == Win32.MEM_COMMIT &&
                mbi.Type == Win32.MEM_PRIVATE &&
                (mbi.Protect & Win32.PAGE_GUARD) == 0 &&
                (mbi.Protect & 0xFF) == Win32.PAGE_READWRITE;

            if (usable) yield return (mbi.BaseAddress, regionSize);

            long next = regionBase + regionSize;
            if (next <= addr) yield break;      // 비정상 응답 방어
            addr = next;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { Win32.CloseHandle(_handle); _handle = IntPtr.Zero; }
        _proc?.Dispose();
        _proc = null;
        lock (_moduleCache) _moduleCache.Clear();
    }
}
