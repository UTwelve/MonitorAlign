using System.Runtime.InteropServices;

namespace MonitorAlign.Native;

/// <summary>
/// 本项目用到的全部 Win32 声明：DWM 材质、显示器枚举、显示排列读写、设备上下文。
/// </summary>
internal static class Win32
{
    // ---------- 窗口消息 ----------
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DISPLAYCHANGE = 0x007E;
    /// <summary>DWM 合成状态变化（用户在设置里开关了"透明效果"）。需要重新应用材质。</summary>
    public const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_MOUSEMOVE = 0x0200;

    public const int HTTRANSPARENT = -1;   // 鼠标穿透
    public const int HTCLIENT = 1;         // 正常接收
    public const int HTCAPTION = 2;

    // ---------- DWM 窗口属性 ----------
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;  // Win11 22H2+
    public const int DWMWA_MICA_EFFECT = 1029;        // Win11 21H2

    // DWM_SYSTEMBACKDROP_TYPE
    public const int DWMSBT_NONE = 0;
    public const int DWMSBT_AUTO = 1;
    public const int DWMSBT_MAINWINDOW = 2;       // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic
    public const int DWMSBT_TABBEDWINDOW = 4;     // Mica Alt

    // DWM_WINDOW_CORNER_PREFERENCE
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    // ---------- 结构体 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;          // POINTL dmPosition 拆开
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    public const uint DM_POSITION = 0x00000020;
    public const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    // ---------- P/Invoke ----------
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrSize);

    /// <summary>玻璃帧扩展范围。-1 表示“无限”，即整窗都归入 DWM 的背景区域。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int LeftWidth, RightWidth, TopHeight, BottomHeight;
    }

    /// <summary>
    /// 把窗口框架扩展到客户区。Mica 只画在这块区域里，
    /// 不调用它的话 DWM 不会为窗口提供任何背景材质。
    /// </summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_NORESET = 0x10000000;
    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const int DISP_CHANGE_RESTART = 1;
    public const int DISP_CHANGE_FAILED = -1;
    public const int DISP_CHANGE_BADMODE = -2;

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public const int MDT_EFFECTIVE_DPI = 0;
    public const int MDT_RAW_DPI = 1;

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    [DllImport("gdi32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    public const int HORZSIZE = 4;   // 物理宽度 mm
    public const int VERTSIZE = 6;   // 物理高度 mm

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>全局鼠标位置（PerMonitorV2 进程拿到的是逻辑坐标，与 DisplayInfo 同一坐标系）。</summary>
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>读取窗口样式位。GWL_STYLE=-16，GWL_EXSTYLE=-20。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_EX_LAYERED = 0x00080000;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // ---- 单实例：把已在运行的自己的窗口提到前台 ----

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>SW_RESTORE=9：最小化了就还原再置前。</summary>
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>从 WM_NCHITTEST 的 lParam 解出屏幕坐标（物理像素）。</summary>
    public static POINT LParamToPoint(IntPtr lParam)
    {
        int v = (int)(long)lParam;
        return new POINT { X = (short)(v & 0xFFFF), Y = (short)((v >> 16) & 0xFFFF) };
    }
}
