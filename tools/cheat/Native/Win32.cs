using System.Runtime.InteropServices;
using System.Text;

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

    // -----------------------------------------------------------------
    //  W11 Day 4 : ESP 오버레이
    //
    //  게임 창의 위치·크기를 읽고, 우리 오버레이 창을 그 위에 겹쳐
    //  클릭이 통과하게 만든다. 게임 프로세스의 렌더링에는 관여하지 않는다
    //  (DirectX 후킹 없음). 순수 외부 방식 그대로다.
    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // 확장 스타일: 클릭 통과 오버레이
    public const int  GWL_EXSTYLE       = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020;   // 마우스 입력이 아래 창으로 통과
    public const long WS_EX_TOOLWINDOW  = 0x00000080;   // Alt+Tab 목록에 안 뜸
    public const long WS_EX_LAYERED     = 0x00080000;
    public const long WS_EX_NOACTIVATE  = 0x08000000;   // 포커스를 빼앗지 않음

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    // -----------------------------------------------------------------
    //  W11 Day 4b : GPU 합성 투명 창 (지연 개선)
    //
    //  WPF AllowsTransparency 는 매 프레임 화면 전체를 CPU 로 그려
    //  UpdateLayeredWindow 로 넘긴다. 1080p 에서 느려 회전 중 박스가 밀렸다.
    //  대신 DWM 이 창 전체를 "유리"로 합성하게 하고(투명 배경),
    //  레이어드 속성은 알파 255 로만 둬서 WPF 가 GPU 로 그리게 한다.
    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    public const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    // -----------------------------------------------------------------
    //  W11 Day 4c : 포커스 보호
    //
    //  오버레이가 게임 포커스를 빼앗으면 Unity 가 마우스 잠금을 풀어
    //  커서가 보이고 발사가 안 된다. 마우스 활성화를 거부하고,
    //  그래도 포커스를 가져가면 게임 창으로 되돌린다.
    // -----------------------------------------------------------------

    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE    = 3;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // -----------------------------------------------------------------
    //  W12 : 트리거봇 발사 입력
    //
    //  SendInput 은 OS 입력 큐에 마우스 이벤트를 넣는다. 게임 프로세스에
    //  아무것도 주입하지 않으며, 맨 앞 창(게임)이 받는다.
    //  INPUT 은 x64 에서 40바이트: type(4) + 패딩(4) + MOUSEINPUT(32).
    //  마우스만 쓰므로 KEYBDINPUT/HARDWAREINPUT 공용체는 선언하지 않는다
    //  (MOUSEINPUT 이 셋 중 가장 커서 크기가 같다).
    // -----------------------------------------------------------------

    public const int  VK_LBUTTON           = 0x01;
    public const uint INPUT_MOUSE          = 0;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int    dx;
        public int    dy;
        public uint   mouseData;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint       type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
