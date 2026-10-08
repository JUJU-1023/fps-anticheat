using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CheatClient.Native;

namespace CheatClient.Core;

/// <summary>
/// 대상 게임 프로세스에 붙어 메모리를 읽고 쓴다. 순수 외부 방식(RPM/WPM).
/// attach 전에 설치 폴더 파일 해시를 프리즈 빌드 지문과 대조한다.
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

    /// <summary>모듈 베이스 주소. 네이티브 포인터 체인의 기준점.</summary>
    public IntPtr GetModuleBase(string moduleName)
    {
        if (_proc == null) throw new InvalidOperationException("attach 되지 않음.");
        foreach (ProcessModule m in _proc.Modules)
            if (string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                return m.BaseAddress;
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
    /// W10 Day2~ 에서 체인이 확정되면 쓴다.
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

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { Win32.CloseHandle(_handle); _handle = IntPtr.Zero; }
        _proc?.Dispose();
        _proc = null;
    }
}
