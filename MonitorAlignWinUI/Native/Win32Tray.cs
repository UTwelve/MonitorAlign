using System;
using System.Runtime.InteropServices;

namespace MonitorAlign.Native;

/// <summary>
/// 托盘图标的原生声明（纯 Win32）。
///
/// 为什么不用 WinForms 的 <c>NotifyIcon</c>：本工程是 WinUI 3（Microsoft.NET.Sdk + UseWinUI），
/// 开 <c>UseWindowsForms</c> 会把一整套 System.Windows.Forms 拖进来，
/// 而且隐式 using 与 XAML 侧的类型名会打架（WPF 版为此专门加过两条 Using Remove）。
/// 托盘本身只是 <c>Shell_NotifyIcon</c> + 一个收消息的消息窗，自己写更短也更可控。
/// </summary>
internal static class Win32Tray
{
    // ---------- Shell_NotifyIcon ----------

    public const int NIM_ADD = 0x00000000;
    public const int NIM_MODIFY = 0x00000001;
    public const int NIM_DELETE = 0x00000002;

    public const int NIF_MESSAGE = 0x00000001;
    public const int NIF_ICON = 0x00000002;
    public const int NIF_TIP = 0x00000004;
    public const int NIF_INFO = 0x00000010;

    public const int NIIF_NONE = 0x00000000;
    public const int NIIF_INFO = 0x00000001;

    /// <summary>
    /// 托盘数据结构。字段顺序与 <c>NOTIFYICONDATAW</c> 逐条对齐 ——
    /// 顺序错一位的表现是"图标出来但气泡/回调全无"。
    /// cbSize 用 <see cref="Marshal.SizeOf"/> 取（= 原生 V4 尺寸，Win7+ 认）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    // ---------- 消息窗（托盘回调的收件人）----------

    public const int WM_NULL = 0x0000;
    public const int WM_DESTROY = 0x0002;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;

    /// <summary>托盘回调消息号（&lt;= WM_USER 会被系统吃掉，取 WM_USER+1）。</summary>
    public const int WM_TRAYCALLBACK = 0x0400 + 1;

    /// <summary>消息专用窗的父窗句柄。</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
                                               int dwStyle, int x, int y, int nWidth, int nHeight,
                                               IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance,
                                               IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>资源管理器重启后托盘图标会失效，靠这个消息重挂。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    // ---------- 图标 ----------

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x0010;
    public const uint LR_DEFAULTSIZE = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType,
                                          int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- 右键菜单 ----------

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    /// <summary>同步返回被点中的菜单项 id，省掉一条 WM_COMMAND 回路。</summary>
    public const uint TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y,
                                              IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
