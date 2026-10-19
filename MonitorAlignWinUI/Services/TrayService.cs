using System;
using System.IO;
using MonitorAlign.Native;

namespace MonitorAlign.Services;

/// <summary>
/// 托盘图标（WinUI 3 没有内建 NotifyIcon，这里用 <c>Shell_NotifyIcon</c> 直接做）。
///
/// 界面无关：只抛"用户想打开窗口 / 想退出"两件事，具体动窗口由 App 决定。
/// </summary>
public interface ITrayService
{
    /// <summary>双击图标或菜单「打开」。</summary>
    event Action OpenRequested;

    /// <summary>菜单「退出」（真退出，不是缩到托盘）。</summary>
    event Action ExitRequested;

    /// <summary>建图标（幂等）。</summary>
    void Show();

    /// <summary>第一次缩进托盘时提示一句去向，之后不再打扰。</summary>
    void ShowBalloonOnce();

    void Dispose();
}

public sealed class TrayService : ITrayService, IDisposable
{
    private const int IdOpen = 1001;
    private const int IdExit = 1002;
    private const string ClassName = "MonitorAlign.TrayWindow";

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private IntPtr _menu;
    private bool _visible;
    private bool _balloonShown;
    private DateTime _lastPopup = DateTime.MinValue;

    /// <summary>
    /// 窗口过程委托必须挂在字段上：交给 Win32 的只是函数指针，
    /// 委托一旦被 GC 回收，下一次消息进来就是进程崩溃。
    /// </summary>
    private Win32Overlay.WndProc _wndProc;

    private uint _taskbarCreated;

    public event Action OpenRequested;
    public event Action ExitRequested;

    public void Show()
    {
        if (_visible) return;

        if (_hwnd == IntPtr.Zero && !CreateMessageWindow())
        {
            Diag.Log("托盘：消息窗创建失败，放弃托盘图标");
            return;
        }

        _hIcon = LoadIcon();
        _menu = BuildMenu();
        _taskbarCreated = Win32Tray.RegisterWindowMessage("TaskbarCreated");

        if (!AddIcon())
        {
            Diag.Log("托盘：Shell_NotifyIcon(NIM_ADD) 失败");
            return;
        }

        _visible = true;
        Diag.Log("托盘图标已创建");
    }

    public void ShowBalloonOnce()
    {
        if (_balloonShown || !_visible) return;
        _balloonShown = true;

        var data = new Win32Tray.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Tray.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32Tray.NIF_INFO,
            szInfo = Loc.TrayBalloonText,
            szInfoTitle = Loc.TrayBalloonTitle,
            dwInfoFlags = Win32Tray.NIIF_INFO,
        };
        Win32Tray.Shell_NotifyIcon(Win32Tray.NIM_MODIFY, ref data);
        Diag.Log("托盘气泡已弹出");
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (_visible)
        {
            var data = new Win32Tray.NOTIFYICONDATA
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Tray.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Win32Tray.Shell_NotifyIcon(Win32Tray.NIM_DELETE, ref data);
            _visible = false;
        }

        if (_menu != IntPtr.Zero) { Win32Tray.DestroyMenu(_menu); _menu = IntPtr.Zero; }
        if (_hIcon != IntPtr.Zero) { Win32Tray.DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
        Win32Tray.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        Diag.Log("托盘图标已移除");
    }

    // ---------------- 内部 ----------------

    private bool AddIcon()
    {
        var data = new Win32Tray.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Tray.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32Tray.NIF_MESSAGE | Win32Tray.NIF_ICON | Win32Tray.NIF_TIP,
            uCallbackMessage = Win32Tray.WM_TRAYCALLBACK,
            hIcon = _hIcon,
            szTip = Loc.TrayTooltip,
        };
        return Win32Tray.Shell_NotifyIcon(Win32Tray.NIM_ADD, ref data);
    }

    private IntPtr BuildMenu()
    {
        IntPtr m = Win32Tray.CreatePopupMenu();
        Win32Tray.AppendMenu(m, Win32Tray.MF_STRING, new IntPtr(IdOpen), Loc.TrayOpen);
        Win32Tray.AppendMenu(m, Win32Tray.MF_SEPARATOR, IntPtr.Zero, null);
        Win32Tray.AppendMenu(m, Win32Tray.MF_STRING, new IntPtr(IdExit), Loc.TrayExit);
        return m;
    }

    private static IntPtr LoadIcon()
    {
        // 托盘图标来自输出目录里的 Assets\app.ico（csproj 里 CopyToOutputDirectory）
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        IntPtr h = File.Exists(path)
            ? Win32Tray.LoadImage(IntPtr.Zero, path, Win32Tray.IMAGE_ICON, 0, 0,
                                  Win32Tray.LR_LOADFROMFILE | Win32Tray.LR_DEFAULTSIZE)
            : IntPtr.Zero;

        if (h == IntPtr.Zero) Diag.Log($"托盘图标加载失败：{path}");
        return h;
    }

    private bool CreateMessageWindow()
    {
        _wndProc = WndProc;                     // 挂字段防 GC

        var wcex = new Win32Tray.WNDCLASSEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Tray.WNDCLASSEX>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = ClassName,
        };
        Win32Tray.RegisterClassEx(ref wcex);    // 已存在就失败，忽略

        _hwnd = Win32Tray.CreateWindowEx(0, ClassName, "", 0, 0, 0, 0, 0,
                                         Win32Tray.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero,
                                         IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            Diag.Log($"托盘：CreateWindowEx 失败 errno={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return _hwnd != IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32Tray.WM_TRAYCALLBACK)
        {
            int mouse = (int)lParam.ToInt64() & 0xFFFF;
            if (mouse == Win32Tray.WM_LBUTTONDBLCLK)
            {
                Diag.Log("托盘：双击 → 打开");
                OpenRequested?.Invoke();
            }
            else if (mouse is Win32Tray.WM_RBUTTONUP or Win32Tray.WM_CONTEXTMENU)
            {
                ShowMenu();
            }
            return IntPtr.Zero;
        }

        // 资源管理器重启：图标没了，补挂一次
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            Diag.Log("托盘：TaskbarCreated → 重新挂图标");
            AddIcon();
            return IntPtr.Zero;
        }

        return Win32Tray.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 弹右键菜单。<c>TrackPopupMenuEx</c> 带 <c>TPM_RETURNCMD</c> 时同步返回选中项 id。
    /// </summary>
    private void ShowMenu()
    {
        // 右键会连发 WM_RBUTTONUP + WM_CONTEXTMENU（取决于 Shell 版本），
        // 不挡一道的话菜单会连弹两次。
        var now = DateTime.Now;
        if ((now - _lastPopup).TotalMilliseconds < 400) return;
        _lastPopup = now;

        Win32Overlay.GetCursorPos(out Win32Overlay.POINT pt);
        Win32Tray.SetForegroundWindow(_hwnd);

        int cmd = Win32Tray.TrackPopupMenuEx(_menu,
            Win32Tray.TPM_LEFTALIGN | Win32Tray.TPM_RIGHTBUTTON | Win32Tray.TPM_RETURNCMD,
            pt.X, pt.Y, _hwnd, IntPtr.Zero);

        // 菜单收尾：不补这一发，菜单在失去焦点时可能赖着不走
        Win32Tray.PostMessage(_hwnd, Win32Tray.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (cmd == IdOpen) { Diag.Log("托盘：菜单 → 打开"); OpenRequested?.Invoke(); }
        else if (cmd == IdExit) { Diag.Log("托盘：菜单 → 退出"); ExitRequested?.Invoke(); }
    }
}
