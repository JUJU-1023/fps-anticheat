using System.Runtime.InteropServices;

namespace CheatClient.Native;

/// <summary>순수 외부 메모리 접근에 필요한 최소한의 Win32 API. DLL 주입은 쓰지 않는다.</summary>
internal static class Win32
{
    [Flags]
    public enum ProcessAccess : uint
    {
        VmOperation      = 0x0008,
        VmRead           = 0x0010,
        VmWrite          = 0x0020,
        QueryInformation = 0x0400,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(ProcessAccess access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr baseAddress, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(
        IntPtr hProcess, IntPtr baseAddress, byte[] buffer, int size, out IntPtr written);

    // -----------------------------------------------------------------
    //  W11 : 메모리 영역 열거 (적 보간기 vtable 스캔용)
    //
    //  QueryInformation 권한만 있으면 된다. attach 시 이미 받고 있다.
    // -----------------------------------------------------------------

    public const uint MEM_COMMIT     = 0x1000;
    public const uint MEM_PRIVATE    = 0x20000;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_GUARD     = 0x100;

    /// <summary>x64 레이아웃(48바이트). PartitionId 뒤 패딩은 Sequential 정렬이 맞춘다.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint   AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint   State;
        public uint   Protect;
        public uint   Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(
        IntPtr hProcess, IntPtr address, out MEMORY_BASIC_INFORMATION info, IntPtr length);

    // -----------------------------------------------------------------
    //  W11 : 키 상태 (우클릭을 누르고 있는 동안만 자동 조준)
    //
    //  전역 키 상태를 읽기만 한다. 게임에 입력을 넣지 않는다.
    // -----------------------------------------------------------------

    public const int VK_RBUTTON = 0x02;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
}
