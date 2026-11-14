namespace WinGLSLScrSaver;

using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;

internal static class Native
{
    [DllImport("user32.dll")]
    internal static extern IntPtr CreateCursor(IntPtr hInst, int xHotspot, int yHotspot, int nWidth, int nHeight, byte[] andPlane, byte[] xorPlane);

    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    internal static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    internal const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    // Display power management: while the screensaver runs, tell Windows the
    // display is in use so the OS power plan cannot blank a monitor mid-run
    // (user report: a monitor occasionally switched off and back on).
    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll")] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static extern int SetWindowLong32(IntPtr hwnd, int index, int newLong);
    internal const int GWL_STYLE = -16;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_VISIBLE = 0x10000000;

    internal static IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : (IntPtr)SetWindowLong32(hwnd, index, (int)value);

    // ---- high-resolution waitable timer (FPS pacing) ----
    // Sleep(1) granularity depends on the system timer resolution, which
    // since Windows 10 2004 is only raised for the FOCUSED process - a
    // screensaver never has focus, so Sleep-based pacing degrades to
    // ~15.6ms ticks. A HIGH_RESOLUTION waitable timer keeps ~0.5ms
    // precision regardless of focus (Windows 10 1803+).
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const long TIMER_QUERY_TICKS = -1;      // relative due time
    private const uint WAIT_OBJECT_0 = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string name,
        uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime,
        int period, IntPtr completionRoutine, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    internal static void SleepHighRes(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return;
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, 0x1FFFFF /* TIMER_ALL_ACCESS */);
        if (timer == IntPtr.Zero)
        {
            // Pre-1803 fallback: coarse sleep.
            System.Threading.Thread.Sleep((int) t.TotalMilliseconds);
            return;
        }
        // Due time is in 100ns units, negative = relative; TimeSpan ticks are
        // also 100ns, so the value maps 1:1.
        long due = -t.Ticks;
        SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
        WaitForSingleObject(timer, uint.MaxValue);
        CloseHandle(timer);
    }
}

