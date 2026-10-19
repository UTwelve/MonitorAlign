using System.Runtime.InteropServices;

namespace MonitorAlign.Native;

/// <summary>
/// 覆盖层专用原生声明（WinUI 3 版）。
///
/// 为什么不并进共享的 <c>Native/Win32.cs</c>：那一份两个版本共用，WPF 版已交付冻结；
/// 这些入口只有 WinUI 3 的覆盖层需要，单独放这里，改动不会波及 WPF 版。
/// </summary>
internal static class Win32Overlay
{
    // ---------- 窗口样式位 ----------
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_WNDPROC = -4;

    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_POPUP = unchecked((int)0x80000000);

    public const int WS_EX_LAYERED = 0x00080000;
    /// <summary>整窗不吃鼠标（泛光层用；标尺层不用它，靠 region 打洞）。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;
    /// <summary>不出现在 Alt+Tab / 任务栏。</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    /// <summary>点击不抢焦点（否则会把自己的主窗口焦点抢走）。</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    // ---------- 单实例唤醒（把已存在的实例提到前台）----------

    public const int SW_HIDE = 0;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>把窗口提到前台（ShowWindow 只管显示态，不带激活）。</summary>
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---------- 区域打洞（逐元素命中）----------

    public const int RGN_OR = 2;

    /// <summary>
    /// 把窗口的形状设为给定区域。**同时裁剪绘制与命中测试**：
    /// 区域之外既画不出来、也不吃鼠标（鼠标落到下面的窗口/桌面）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    /// <summary>查询窗口当前区域：0=失败 1=NULLREGION(空) 2=SIMPLEREGION 3=COMPLEXREGION。</summary>
    [DllImport("user32.dll")]
    public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    public static extern int GetRgnBox(IntPtr hrgn, out RECT lprc);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public const int NULLREGION = 1;
    public const int SIMPLEREGION = 2;
    public const int COMPLEXREGION = 3;

    /// <summary>
    /// 当前窗口区域的一句话描述 —— 诊断"打洞丢了"用。
    /// 丢区域的表现是：美术整块不画、鼠标也不归本窗口（区域同时管绘制与命中）。
    /// </summary>
    public static string DescribeRegion(IntPtr hwnd)
    {
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        try
        {
            int kind = GetWindowRgn(hwnd, rgn);
            switch (kind)
            {
                case NULLREGION: return "空区域(整窗不可见/不可命中)";
                case SIMPLEREGION:
                case COMPLEXREGION:
                    GetRgnBox(rgn, out RECT box);
                    return $"{(kind == COMPLEXREGION ? "复合" : "单")}区域 [{box.Left},{box.Top}]-[{box.Right},{box.Bottom}]";
                default: return $"查询失败({kind})";
            }
        }
        finally { DeleteObject(rgn); }
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    // ---------- DWM：去圆角 / 去描边 / 玻璃（覆盖层透明） ----------

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1;

    /// <summary>让 DWM 按深色处理这个窗口（玻璃/描边/圆角的着色跟着走）。</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>DwmSetWindowAttribute 的"无颜色"哨兵（去掉窗口描边）。</summary>
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrSize);

    public const int DWM_BB_ENABLE = 0x1;
    public const int DWM_BB_BLURREGION = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public int dwFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);

    // ---------- 窗口消息子类化（拦 WM_ERASEBKGND，别让系统用底色擦背景） ----------

    /// <summary>
    /// 把键盘焦点给某个窗。
    ///
    /// 标尺能不能接到鼠标，取决于 **XAML 输入站点有没有焦点**（见
    /// <see cref="Overlay.FocusIsland"/> 那节）：没有焦点时第一次点击会被
    /// "岛子取得焦点"这一步吃掉，用户点了没反应，再点一次才正常。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    public const int WM_ERASEBKGND = 0x0014;
    public const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int VK_SHIFT = 0x10;

    /// <summary>取某键当前状态；高位（0x8000）为 1 = 正按住。用于 Shift = 10px 步进。</summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd,
                                               uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>找子窗。类名传 null = 取第一个子窗。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter,
                                             string lpszClass, string lpszWindow);

    /// <summary>取窗口类名（挂钩子时打进日志，便于确认挂的到底是谁）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName,
                                          int nMaxCount);

    /// <summary>本线程的焦点窗。WinUI 3 里键盘焦点通常落在 XAML 岛子窗上，不是顶层窗。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    /// <summary>hWndChild 是否为 hWndParent 的后代。</summary>
    [DllImport("user32.dll")]
    public static extern bool IsChild(IntPtr hWndParent, IntPtr hWndChild);

    // ---------- 线程级消息钩子（WH_GETMESSAGE）----------

    /// <summary>
    /// 抢在**消息被派发之前**看一眼队列。
    ///
    /// 为什么非它不可：WinUI 3 的 XAML 输入站点会在派发前把方向键吃掉
    /// （XY 焦点导航）——实测 Esc 与 Shift 都能到达窗口过程，唯独 ↑↓←→ **连消息都不出现**。
    /// 窗口过程这一层不管怎么挂都晚了，必须前移到 GetMessage 这一级。
    /// 本线程钩子不需要注入 DLL，托管代码可直接装。
    /// </summary>
    public const int WH_GETMESSAGE = 3;
    public const int PM_REMOVE = 0x0001;
    public const int WM_NULL = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn,
                                                 IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---------- OS 层 DispatcherQueue（Windows.UI.Composition.Compositor 的前置条件） ----------

    /// <summary>
    /// CoreMessaging 的 <c>CreateDispatcherQueueController</c>。
    ///
    /// 为什么需要：覆盖层要一个"全透明"的 SystemBackdrop，而
    /// <c>ICompositionSupportsSystemBackdrop.SystemBackdrop</c> 只吃 **OS 合成器**的
    /// <c>Windows.UI.Composition.CompositionBrush</c>；构造那个 Compositor 时它要求
    /// 当前线程有 <c>Windows.System.DispatcherQueue</c> ——WinUI 3 建的是
    /// <c>Microsoft.UI.Dispatching.DispatcherQueue</c>，OS 层看不到，
    /// 于是直接 new 会抛 "The caller must initialize DispatcherQueue on this thread"。
    /// 这里按 WinUIEx 的既有做法补一个（仅当 OS 层确实没有时）。
    /// </summary>
    internal static class CoreMessaging
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions
        {
            public int dwSize;
            public int threadType;      // DQTYPE_THREAD_CURRENT = 2
            public int apartmentType;   // DQTAT_COM_STA = 2
        }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(
            DispatcherQueueOptions options, out IntPtr dispatcherQueueController);

        private static IntPtr _controller;

        public static void EnsureDispatcherQueue()
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || _controller != IntPtr.Zero)
                return;

            var options = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = 2,
                apartmentType = 2,
            };
            CreateDispatcherQueueController(options, out _controller);
        }
    }
}
