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
}
