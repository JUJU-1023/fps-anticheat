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
}
