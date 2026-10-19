using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MonitorAlign.Native;
using WinRT.Interop;

namespace MonitorAlign.Overlay;

/// <summary>
/// 覆盖层窗口的公共底子（对齐标尺层 / 边缘泛光层都用）。
/// 职责：无边框无标题栏、置顶、不进 Alt+Tab、点击不抢焦点、逐元素命中。
///
/// **WinUI 3 与 WPF 最大的差别正在这里。**
/// WPF 版用 <c>AllowsTransparency + Background=null</c>：DWM 按逐像素 alpha 做命中测试，
/// 没画东西的地方鼠标天然穿到桌面，"只有标尺可点"是白送的。
/// WinUI 3 的窗口内容由 DirectComposition 画，窗口本身不掌握像素 alpha，
/// **没有等价物**。这里的办法是 <see cref="Win32Overlay.SetWindowRgn"/> 按交互矩形打洞：
/// 窗口区域同时裁剪"绘制"与"命中测试"，洞里的鼠标直接落到下面的窗口/桌面。
///
/// 由此带来的两条硬约束（改覆盖层时别踩）：
///  1. 区域之外**画不出来**。所以柔和渐变（泛光、阴影）必须放进另一个整窗穿透
///     （WS_EX_TRANSPARENT）的层，不能和交互元素共用一个窗口。
///  2. 交互矩形变了必须重新 <see cref="ApplyHitRegion"/> —— 不调就还是旧形状。
/// </summary>
public abstract class OverlayWindowBase : Window
{
    /// <summary>窗口句柄（构造后即可用）。</summary>
    protected IntPtr Handle { get; private set; }

    /// <summary>
    /// **画出来的**矩形（窗口 DIP，左上原点）—— 打洞用的就是这一份，
    /// 因为 <see cref="Win32Overlay.SetWindowRgn"/> 同时裁剪绘制与命中测试：
    /// 区域之外**画不出来**。所以每一件美术（线、三角、面板、标签）都必须登记，
    /// 漏一件就被裁掉（第一版只登记了按钮条，于是线/三角/标签全消失）。
    /// </summary>
    protected readonly List<Windows.Foundation.Rect> RegionRects = new();

    /// <summary>其中**吃鼠标**的那些（通常是按钮条面板）。验收脚本按它判断"该归本层"。</summary>
    protected readonly List<Windows.Foundation.Rect> HitRects = new();

    /// <summary>是否启用「按命中矩形打洞」。关掉即整窗吃鼠标（负控 / 特殊层用）。</summary>
    private bool _punchEnabled = true;

    /// <summary>拖动期间临时放宽成「整窗可命中」，松手后收回（见 <see cref="SetWidened"/>）。</summary>
    private bool _widened;

    /// <summary>
    /// 拖动期间冻结窗口区域：此时一律不碰 <c>SetWindowRgn</c>。
    ///
    /// ⚠ 这是实拍逼出来的：拖动中每帧都会 <c>Render()</c>，而 <c>Render()</c>
    /// 末尾要 <c>ApplyHitRegion()</c> 让区域跟着标尺挪 —— 于是每帧一次
    /// <c>SetWindowRgn</c>。**改窗口区域会当场打断 XAML 的指针捕获**，
    /// 表现为起手后 3ms 就 "拖动结束"，100px 的手势只跟到 20px。
    /// 拖动期间区域本来就已被 <see cref="SetWidened"/> 放宽成整窗，重设纯属多余且有副作用。
    /// </summary>
    protected bool RegionFrozen { get; set; }

    /// <summary>本层是否带 <c>WS_EX_NOACTIVATE</c>（点击不抢焦点）。标尺层要键盘，所以不带。</summary>
    private bool _noActivate;

    /// <summary>
    /// 是否套用 DWM「玻璃」（<c>DwmExtendFrameIntoClientArea</c> + 空模糊区的
    /// <c>DwmEnableBlurBehindWindow</c>）。默认开；<c>--glass off</c> 可关掉做 A/B。
    ///
    /// 为什么要留这个开关：实测这几步会往窗口里**叠一层带强调色味儿的着色**
    /// （面板实测偏蓝偏亮，而线画出来的强调色是原值）。透明本身不一定依赖它，
    /// 所以得能一变量一地验。
    /// </summary>
    public static bool GlassEnabled { get; set; } = true;

    /// <summary>
    /// 强制所有层都带 <c>WS_EX_NOACTIVATE</c>（<c>--act off</c>）。
    ///
    /// 这是个**诊断开关**，不是为了功能：负控（<c>--punch none</c>，整窗吃鼠标）
    /// 一度失去判别力，需要把"可激活"这个变量单独摘出来 A/B。
    /// </summary>
    public static bool ForceNoActivate { get; set; }

    /// <summary>
    /// 是否给覆盖层加 <c>WS_EX_LAYERED</c>（<c>--layered off</c> 可关，仅诊断用）。
    /// 分层窗口的命中判定与普通窗口不同，这个开关用来确认"空白处为什么穿透"。
    /// </summary>
    public static bool LayeredEnabled { get; set; } = true;

    /// <summary><c>--fill 0..255</c>：给画布铺一层该 alpha 的纯黑底，做命中测试 A/B。</summary>
    public static int FillAlpha { get; set; }

    /// <summary>窗口过程委托与旧过程指针：委托必须挂字段，否则被 GC 回收后回调即崩。</summary>
    private Win32Overlay.WndProc _wndProc;
    private IntPtr _prevWndProc;

    /// <summary>
    /// XAML 岛子窗（WinUI 3 把 XAML 内容放在顶层窗的**子窗**里）。
    /// 键盘消息送到的是它，不是顶层窗 —— 所以键盘钩子必须挂到这些子窗上，
    /// 见 <see cref="HookIslandKeyboard"/>。
    /// </summary>
    private sealed class Subclass
    {
        public Win32Overlay.WndProc Proc;   // 委托必须活着，否则回调即崩
        public IntPtr Prev;
    }

    private readonly Dictionary<IntPtr, Subclass> _subs = new();

    /// <summary>线程消息钩子的委托与句柄（委托同样必须挂字段防 GC）。</summary>
    private Win32Overlay.HookProc _msgHook;
    private IntPtr _msgHookHandle;

    /// <summary>Shift 是否按住（钩子自己记账，见 ThreadMsgHook）。</summary>
    private bool _shiftDown;

    /// <summary>WinUI 3 的 XAML 岛子窗类名（Windows App SDK 1.5+）。</summary>
    private const string IslandClass = "Microsoft.UI.Content.DesktopChildSiteBridge";

    /// <summary>XAML 真正的输入窗（岛子窗的子窗，焦点在它身上才收得到键鼠）。</summary>
    private const string InputSiteClass = "InputSiteWindowClass";

    /// <summary>窗口当前 DPI 缩放（物理像素 ÷ DIP）。</summary>
    public double DpiScale
    {
        get
        {
            uint dpi = Win32Overlay.GetDpiForWindow(Handle);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
    }

    /// <summary>
    /// 覆盖层窗口初始化。<b>必须在派生类 InitializeComponent() 之后调用</b>：
    /// 这里会读 AppWindow（XAML 加载后才就绪）。
    ///
    /// <paramref name="noActivate"/>：
    ///   真 = 带 <c>WS_EX_NOACTIVATE</c>，点击不抢焦点（泛光层要这个：它浮在所有窗口上，
    ///       鼠标扫过不该打断用户当前的操作）。
    ///   假 = 可被激活（标尺层要这个）——**XAML 焦点依赖窗口激活**，
    ///       带 NOACTIVATE 的窗口拿不到键盘，方向键微调与 Esc 退出就全哑了。
    ///       WPF 版标尺层也是可激活的（Loaded 里 Focus()），行为保持一致。
    /// </summary>
    protected void InitOverlay(bool noActivate = false, bool keyboardEnabled = true)
    {
        Handle = WindowNative.GetWindowHandle(this);
        _noActivate = noActivate;
        Closed += (_, _) => UnhookThreadKeyboard();

        // 透明背景：全靠 SystemBackdrop，XAML 侧再不许有底色
        SystemBackdrop = new TransparentBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            // 关标题栏与边框（必须在改 Win32 样式位之前，否则 DWM 会补画一套）
            try { p.SetBorderAndTitleBar(false, false); } catch { }
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }

        // 清掉 Win32 样式位：SetBorderAndTitleBar 之后仍会残留 WS_CAPTION 等，
        // 留着的话窗口会有一圈看不见的边框参与命中测试与绘制
        long style = Win32Overlay.GetWindowLongPtr(Handle, Win32Overlay.GWL_STYLE).ToInt64();
        style &= ~(long)(Win32Overlay.WS_CAPTION | Win32Overlay.WS_THICKFRAME
                         | Win32Overlay.WS_MINIMIZEBOX | Win32Overlay.WS_MAXIMIZEBOX
                         | Win32Overlay.WS_SYSMENU);
        Win32Overlay.SetWindowLongPtr(Handle, Win32Overlay.GWL_STYLE, new IntPtr(style));

        long ex = Win32Overlay.GetWindowLongPtr(Handle, Win32Overlay.GWL_EXSTYLE).ToInt64();
        ex |= Win32Overlay.WS_EX_TOOLWINDOW;        // 不进 Alt+Tab / 任务栏
        if (LayeredEnabled) ex |= Win32Overlay.WS_EX_LAYERED;  // 分层：让 DWM 按 alpha 合成
        if (noActivate || ForceNoActivate) ex |= Win32Overlay.WS_EX_NOACTIVATE;   // 泛光层：点击不抢焦点
        Win32Overlay.SetWindowLongPtr(Handle, Win32Overlay.GWL_EXSTYLE, new IntPtr(ex));

        // 去圆角、去描边：Win11 默认给顶层窗口加圆角与 1px 描边，覆盖层上会露馅
        int pref = Win32Overlay.DWMWCP_DONOTROUND;
        Win32Overlay.DwmSetWindowAttribute(Handle, Win32Overlay.DWMWA_WINDOW_CORNER_PREFERENCE,
                                           ref pref, sizeof(int));
        int noColor = Win32Overlay.DWMWA_COLOR_NONE;
        Win32Overlay.DwmSetWindowAttribute(Handle, Win32Overlay.DWMWA_BORDER_COLOR,
                                           ref noColor, sizeof(int));

        // 覆盖层是浮在任意桌面内容之上的 HUD，着色不该随系统明暗摇摆 —— 一律按深色让 DWM 处理
        int dark = 1;
        Win32Overlay.DwmSetWindowAttribute(Handle, Win32Overlay.DWMWA_USE_IMMERSIVE_DARK_MODE,
                                           ref dark, sizeof(int));

        // 让样式改动立即生效，并置顶显示
        Win32Overlay.SetWindowPos(Handle, Win32Overlay.HWND_TOPMOST, 0, 0, 0, 0,
            Win32Overlay.SWP_NOMOVE | Win32Overlay.SWP_NOSIZE
            | Win32Overlay.SWP_NOACTIVATE | Win32Overlay.SWP_FRAMECHANGED);

        ApplyGlass();
        HookWndProc();
        if (keyboardEnabled) HookThreadKeyboard();
    }

    /// <summary>
    /// DWM 玻璃：让客户区按 alpha 合成（不铺不透明底）。
    ///
    /// <c>DwmEnableBlurBehindWindow</c> 配一个**空**模糊区（矩形 -2,-2,-1,-1 不含任何像素）
    /// 是拿透明窗口的经典手法：不要模糊，只要"玻璃"（alpha 合成）。
    /// 这一步与 <see cref="TransparentBackdrop"/> 缺一不可 —— 只有透明画刷时，
    /// 系统仍可能按不透明底色合成（浅色主题下尤其明显，会看到一片白）。
    /// </summary>
    private void ApplyGlass()
    {
        if (!GlassEnabled) return;

        var margins = new Win32Overlay.MARGINS();
        Win32Overlay.DwmExtendFrameIntoClientArea(Handle, ref margins);

        IntPtr empty = Win32Overlay.CreateRectRgn(-2, -2, -1, -1);
        var bb = new Win32Overlay.DWM_BLURBEHIND
        {
            dwFlags = Win32Overlay.DWM_BB_ENABLE | Win32Overlay.DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = empty,
            fTransitionOnMaximized = false,
        };
        Win32Overlay.DwmEnableBlurBehindWindow(Handle, ref bb);
        Win32Overlay.DeleteObject(empty);
    }

    /// <summary>
    /// 子类化窗口过程。只为拦两件事：
    ///  · <c>WM_ERASEBKGND</c> —— 返回 1（"我擦过了"），不让系统用主题底色擦背景，
    ///    否则透明窗口会先被刷一层不透明色。
    ///  · <c>WM_DWMCOMPOSITIONCHANGED</c> —— 显示模式切换 / DWM 重启后玻璃设置会丢，补回。
    /// </summary>
    private void HookWndProc()
    {
        _wndProc = Hook;
        _prevWndProc = Win32Overlay.SetWindowLongPtr(
            Handle, Win32Overlay.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
    }

    private IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Overlay.WM_ERASEBKGND:
                return new IntPtr(1);
            case Win32Overlay.WM_DWMCOMPOSITIONCHANGED:
                ApplyGlass();
                return IntPtr.Zero;
            case Win32Overlay.WM_KEYDOWN:
            case Win32Overlay.WM_SYSKEYDOWN:
                // 键盘在**窗口过程**这一层接，不走 XAML 焦点 —— 见 OnRawKeyDown 的注释。
                if (OnRawKeyDown((int)wParam.ToInt64(),
                                 (Win32Overlay.GetKeyState(Win32Overlay.VK_SHIFT) & 0x8000) != 0))
                    return IntPtr.Zero;
                return Win32Overlay.CallWindowProc(_prevWndProc, hwnd, msg, wParam, lParam);
            default:
                return Win32Overlay.CallWindowProc(_prevWndProc, hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// 在 XAML 岛子窗上再挂一层窗口过程。
    ///
    /// ⚠ 这一步是 WinUI 3 特有的、必须的（实测出来的，别删）：
    /// WinUI 3 的 XAML 内容不在顶层窗里，而在顶层窗的**子窗**
    /// （类名 <c>Microsoft.UI.Content.DesktopChildSiteBridge</c>，做命中测试时
    /// <c>WindowFromPoint</c> 返回的就是它）。**键盘消息只发给焦点所在的那个窗**，
    /// 也就是这个子窗 —— 顶层窗的窗口过程根本见不到 WM_KEYDOWN。
    /// 于是只挂顶层窗时：鼠标拖动/点按全正常，Esc 与方向键却完全没反应
    /// （验收脚本 6 条断言里只有键盘那两条红）。
    ///
    /// 子窗是 XAML 初始化之后才建的，所以这里做成**幂等 + 可重试**：
    /// 找不到就什么也不做，下次（拖动起手时）再试。
    /// </summary>
    public void HookIslandKeyboard()
    {
        // 两个候选都挂：① 按类名找的直接子窗；② **当前焦点窗**（只要它是本窗的后代）。
        //
        // 实测这两个不是同一个窗：类名子窗 = 0x30096E，而焦点窗 = 0x2609E8。
        // 键盘消息发给**焦点窗**，只挂 ① 时 Esc 与方向键依旧全哑，
        // 于是改成"见一个挂一个"，谁先收到按键算谁的。
        HookChildWindow(Win32Overlay.FindWindowEx(Handle, IntPtr.Zero, IslandClass, null), "类名子窗");

        IntPtr focus = Win32Overlay.GetFocus();
        if (focus != IntPtr.Zero && focus != Handle && Win32Overlay.IsChild(Handle, focus))
            HookChildWindow(focus, "焦点窗");
    }

    /// <summary>给一个子窗挂键盘钩子（幂等：同一个 hwnd 不重复挂）。</summary>
    private void HookChildWindow(IntPtr h, string how)
    {
        if (h == IntPtr.Zero || h == Handle || _subs.ContainsKey(h)) return;

        var cls = new System.Text.StringBuilder(256);
        Win32Overlay.GetClassName(h, cls, cls.Capacity);

        var s = new Subclass { Proc = IslandHook };
        s.Prev = Win32Overlay.SetWindowLongPtr(
            h, Win32Overlay.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(s.Proc));
        _subs[h] = s;

        Diag.Log($"键盘钩子已挂（{how}）hwnd=0x{h.ToInt64():X} class={cls}");
    }

    /// <summary>
    /// 装线程级消息钩子（幂等）。
    ///
    /// ⚠ 方向键必须靠这一层：XAML 的输入站点在**派发之前**就把 ↑↓←→ 吃掉了，
    /// 窗口过程那一层（含上面这些子窗钩子）根本见不到它们；Esc 与 Shift 倒是能到。
    /// 本线程钩子在 GetMessage 时就先看到消息，比输入站点早。
    /// </summary>
    public void HookThreadKeyboard()
    {
        if (_msgHookHandle != IntPtr.Zero) return;

        _msgHook = ThreadMsgHook;                 // 委托必须挂字段，否则回调即崩
        _msgHookHandle = Win32Overlay.SetWindowsHookEx(
            Win32Overlay.WH_GETMESSAGE, _msgHook, IntPtr.Zero, Win32Overlay.GetCurrentThreadId());

        Diag.Log($"线程消息钩子已挂 hhk=0x{_msgHookHandle.ToInt64():X} " +
                 $"tid={Win32Overlay.GetCurrentThreadId()}");
    }

    /// <summary>
    /// 把键盘焦点塞进 XAML 输入站点。
    ///
    /// 为什么非它不可：**"第一次点击没反应"的根因就在这里**。
    /// XAML 的指针事件只在**输入站点拿到焦点之后**才派发；没有焦点时，
    /// 那第一次点击恰好被用来"让站点取得焦点"，于是被吃掉 —— 从第二次起一直正常
    /// （验收脚本里那圈"预热点击"就是这么来的）。
    /// 提前把焦点给进去，第一次点击就是真点击。
    ///
    /// 目标优先 XAML 的输入窗（<c>InputSiteWindowClass</c>），找不到再退回岛子窗
    /// （<c>DesktopChildSiteBridge</c>）—— 焦点在谁身上就是谁能收键鼠。
    /// </summary>
    public void FocusIsland()
    {
        if (_noActivate) return;

        IntPtr island = Win32Overlay.FindWindowEx(Handle, IntPtr.Zero, IslandClass, null);
        IntPtr site = island != IntPtr.Zero
            ? Win32Overlay.FindWindowEx(island, IntPtr.Zero, InputSiteClass, null)
            : IntPtr.Zero;
        IntPtr target = site != IntPtr.Zero ? site : island;
        if (target == IntPtr.Zero) return;

        IntPtr before = Win32Overlay.GetFocus();
        IntPtr got = Win32Overlay.SetFocus(target);
        IntPtr after = Win32Overlay.GetFocus();
        Diag.Log($"焦点给岛子：目标=0x{target.ToInt64():X} "
                 + $"{(site != IntPtr.Zero ? "(输入站点)" : "(岛子窗)")} "
                 + $"SetFocus=0x{got.ToInt64():X} 焦点 0x{before.ToInt64():X} → 0x{after.ToInt64():X}"
                 + (after == target ? " ✓" : " ⚠ 没拿到"));
    }

    /// <summary>卸掉线程消息钩子（关窗前调，免得回调打到已销毁的窗口上）。</summary>
    protected void UnhookThreadKeyboard()
    {
        if (_msgHookHandle == IntPtr.Zero) return;
        Win32Overlay.UnhookWindowsHookEx(_msgHookHandle);
        _msgHookHandle = IntPtr.Zero;
        _msgHook = null;
    }

    private IntPtr ThreadMsgHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt64() == Win32Overlay.PM_REMOVE)
        {
            var msg = Marshal.PtrToStructure<Win32Overlay.MSG>(lParam);
            int vk0 = (int)msg.wParam.ToInt64();

            if (msg.message is Win32Overlay.WM_KEYDOWN or Win32Overlay.WM_KEYUP)
                Diag.Log($"钩子见到 msg=0x{msg.message:X} vk=0x{vk0:X} hwnd=0x{msg.hwnd.ToInt64():X} " +
                         $"归我={(msg.hwnd == Handle || Win32Overlay.IsChild(Handle, msg.hwnd))}");

            // Shift 自己记账，别只读 GetKeyState：
            // 钩子是在消息**取出**时跑的，Shift 的按下可能还没被处理进线程按键状态，
            // 实测 Shift+↓ 只走了 1px（判成没按 Shift）。
            // 两个来源取或：记账优先，GetKeyState 兜住"进本窗前就按住"的情况。
            if (vk0 == Win32Overlay.VK_SHIFT)
            {
                if (msg.message == Win32Overlay.WM_KEYDOWN
                    || msg.message == Win32Overlay.WM_SYSKEYDOWN) _shiftDown = true;
                else if (msg.message == Win32Overlay.WM_KEYUP
                         || msg.message == Win32Overlay.WM_SYSKEYUP) _shiftDown = false;
            }

            // 只管"发给本覆盖层"的键：同一线程上还挂着别的覆盖层，各管各的
            if ((msg.message == Win32Overlay.WM_KEYDOWN
                 || msg.message == Win32Overlay.WM_SYSKEYDOWN)
                && (msg.hwnd == Handle || Win32Overlay.IsChild(Handle, msg.hwnd)))
            {
                int vk = (int)msg.wParam.ToInt64();
                bool shift = _shiftDown
                             || (Win32Overlay.GetKeyState(Win32Overlay.VK_SHIFT) & 0x8000) != 0;

                if (OnRawKeyDown(vk, shift))
                {
                    // 吃掉：把消息改写成 WM_NULL，派发出去也没人理。
                    // 不改的话 XAML 会再收到一次（方向键还会去跑焦点导航）。
                    msg.message = Win32Overlay.WM_NULL;
                    Marshal.StructureToPtr(msg, lParam, false);
                }
            }
        }

        return Win32Overlay.CallNextHookEx(_msgHookHandle, code, wParam, lParam);
    }

    /// <summary>子窗的窗口过程：只挑键盘消息，其余一律放行。</summary>
    private IntPtr IslandHook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 0x100..0x109 = WM_KEY*/WM_CHAR/WM_DEADCHAR/WM_SYSKEY*/WM_SYSCHAR…
        // 全段都打：↓ 与 Esc 一直"没反应"，得先看清它们到底是**没来**，
        // 还是被翻译成别的消息（例如 WM_SYSKEYDOWN）才漏掉的。
        if (msg >= 0x0100 && msg <= 0x0109)
            Diag.Log($"按键消息 msg=0x{msg:X} vk=0x{(int)wParam.ToInt64():X} hwnd=0x{hwnd.ToInt64():X}");

        if (msg == Win32Overlay.WM_KEYDOWN || msg == Win32Overlay.WM_SYSKEYDOWN)
        {
            if (OnRawKeyDown((int)wParam.ToInt64(),
                             (Win32Overlay.GetKeyState(Win32Overlay.VK_SHIFT) & 0x8000) != 0))
                return IntPtr.Zero;
        }

        // 鼠标**不**在这一层接：实测 WM_LBUTTONDOWN 根本不到岛子窗的窗口过程
        // （XAML 把输入放在更深的一层窗口上）。鼠标统一走线程消息钩子，见 ThreadMsgHook。
        return Win32Overlay.CallWindowProc(_subs[hwnd].Prev, hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 原始键盘输入（虚拟键码 + Shift 是否按住）。返回 true = 已处理（消息就此吃掉）。
    ///
    /// ⚠ 为什么不走 XAML 的 <c>KeyDown</c>：要让 XAML 收到键，必须先有一个真实可聚焦
    /// 控件拿到焦点。试过"藏一枚 1×1 全透明 Button 当焦点容器"，实测
    /// <c>Button.Focus()</c> 恒返回 false —— 窗口明明已是前景窗。
    /// 而这里本来就已经子类化了窗口过程，键盘消息必然先经过它，
    /// 于是直接在这一层处理：不依赖 XAML 焦点引擎，少一层不确定性。
    ///
    /// 前提只有一条：**本窗得是前景窗**（所以覆盖层不带 WS_EX_NOACTIVATE），
    /// 且 <see cref="HookIslandKeyboard"/> 已把钩子挂到岛子窗上。
    /// </summary>
    protected virtual bool OnRawKeyDown(int virtualKey, bool shiftDown) => false;

    /// <summary>把窗口挪到指定虚拟桌面矩形（物理像素）。</summary>
    protected void MoveTo(int left, int top, int width, int height)
        => AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(left, top, width, height));

    /// <summary>
    /// 按 <see cref="RegionRects"/> 重建窗口形状。
    /// 空集合 = 整窗不可命中（退化成纯显示层）。
    /// </summary>
    protected void ApplyHitRegion()
    {
        if (RegionFrozen) return;               // 拖动中：任何 SetWindowRgn 都会打断指针捕获
        if (!_punchEnabled || _widened || RegionRects.Count == 0)
        {
            ResetRegion(_punchEnabled ? "拖动放宽" : "负控/整窗穿透");
            return;
        }

        double s = DpiScale;
        IntPtr total = Win32Overlay.CreateRectRgn(0, 0, 0, 0);
        foreach (var r in RegionRects)
        {
            // 向外取整，别让抗锯齿边缘被切掉
            IntPtr one = Win32Overlay.CreateRectRgn(
                (int)Math.Floor(r.X * s),
                (int)Math.Floor(r.Y * s),
                (int)Math.Ceiling((r.X + r.Width) * s),
                (int)Math.Ceiling((r.Y + r.Height) * s));
            Win32Overlay.CombineRgn(total, total, one, Win32Overlay.RGN_OR);
            Win32Overlay.DeleteObject(one);
        }

        // SetWindowRgn 成功后区域归系统所有，不能再 DeleteObject(total)
        Win32Overlay.SetWindowRgn(Handle, total, true);
        Diag.Log($"命中区域已更新：{RegionRects.Count} 块 @{s:0.##}x → {Win32Overlay.DescribeRegion(Handle)}");
    }

    /// <summary>
    /// 把区域清成"整窗"（默认区域）。
    ///
    /// ⚠ 这里刻意把返回值与 GetLastError 打进日志：负控（<c>--punch none</c>）
    /// 曾出现"整窗吃鼠标"没生效 —— 只有把这一句的实际结果看清楚，
    /// 才能分辨是"调用失败"还是"调用成功但命中另有其因"。
    /// </summary>
    private void ResetRegion(string why)
    {
        Marshal.SetLastPInvokeError(0);
        int ok = Win32Overlay.SetWindowRgn(Handle, IntPtr.Zero, true);   // 返回非 0 = 成功
        Diag.Log($"区域清成整窗（{why}）：ok={ok} err={Marshal.GetLastPInvokeError()} → {RegionState}");
    }

    /// <summary>当前窗口区域的一句话描述（验收脚本据它诊断"打洞还在不在"）。</summary>
    public string RegionState => Win32Overlay.DescribeRegion(Handle);

    /// <summary>
    /// 拖动期间把区域临时放宽到整窗，松手后收回。
    ///
    /// ⚠ 这一步是 WinUI 3 特有的、必须的。窗口区域同时裁剪**绘制与命中**：
    /// 按钮条之外的部分是"洞"，鼠标一移到洞里就落到下面的窗口上，
    /// XAML 的指针捕获并不能跨窗口区域继续收到 PointerMoved ——
    /// 于是表现为"拖两下就断了"。WPF 版没有这个问题（透明窗口不存在洞）。
    ///
    /// 放宽期间整屏吃鼠标，这与任何拖拽的语义一致（拖动时不该能点到别的东西）。
    /// </summary>
    public void SetWidened(bool on)
    {
        if (_widened == on) return;
        _widened = on;
        ApplyHitRegion();
        Diag.Log($"命中区域{(on ? "放宽为整窗" : "收回为打洞")} → {RegionState}");
    }

    /// <summary>
    /// 激活本层并请求键盘焦点（方向键微调 / Esc 退出需要）。
    /// 只有 <c>InitOverlay(noActivate: false)</c> 的层调它才有意义。
    /// </summary>
    public void TakeKeyboard()
    {
        if (_noActivate) return;
        Activate();
        FocusIsland();          // 光激活顶层窗不够：输入站点没焦点，第一次点击会被吃掉
    }

    /// <summary>
    /// 跟着内容元素的尺寸变化重设区域。
    ///
    /// ⚠ 这条是被实拍逼出来的：<c>SetWindowRgn</c> 的区域会被**窗口客户区裁剪**。
    /// 窗口刚创建/刚 MoveAndResize 时尺寸可能还是缺省值（甚至 0），此时设进去的区域
    /// 会被裁成空 —— 而空区域**不会再自己长回来**，表现就是覆盖层整块不画、
    /// 鼠标也不归它（区域同时管绘制与命中）。四方位里随机中招，因为窗口何时拿到
    /// 最终尺寸是时序问题。
    ///
    /// 所以：区域必须跟着尺寸重设一次（尺寸定下来之后的这次才是有效的）。
    /// </summary>
    protected void TrackRegionWith(FrameworkElement content)
    {
        // 顺手再挂一次岛子窗钩子：子窗是 XAML 初始化之后才建的，
        // 第一次 Present 时可能还不存在；鼠标/键盘都靠它，多挂几次不亏（幂等）。
        content.SizeChanged += (_, _) => { ApplyHitRegion(); HookIslandKeyboard(); };
        Activated += (_, _) => { ApplyHitRegion(); HookIslandKeyboard(); };
    }

    /// <summary>置顶（别的置顶窗口可能压上来，重新对齐会话时再顶一次）。</summary>
    public void BringToTop()
        => Win32Overlay.SetWindowPos(Handle, Win32Overlay.HWND_TOPMOST, 0, 0, 0, 0,
            Win32Overlay.SWP_NOMOVE | Win32Overlay.SWP_NOSIZE | Win32Overlay.SWP_NOACTIVATE);

    /// <summary>
    /// 开/关「按命中矩形打洞」。
    ///
    /// 关掉 = 不打洞、也不设 WS_EX_TRANSPARENT → **整窗吃鼠标**。
    /// 配 <c>--punch none</c> 当验收负控：此时空白处也必须归本层，
    /// 探测结果必须与开洞时相反；若两者一样，说明探测方法本身没在判别。
    ///
    /// 注意别把这里的语义和 <see cref="SetClickThrough"/> 搞混：
    /// <c>WS_EX_TRANSPARENT</c> 是"整窗不参与命中"（泛光层要的），
    /// 不是"整窗吃鼠标"。第一版就写反了，负控直接把结果翻了过来。
    /// </summary>
    public void SetPunchEnabled(bool on)
    {
        _punchEnabled = on;
        ApplyHitRegion();
        RefreshFrame();
    }

    /// <summary>
    /// 整窗点击穿透（泛光层用：只显示，永不吃鼠标）。
    /// 与「打洞」是两条路：打洞靠窗口区域，整窗穿透靠 WS_EX_TRANSPARENT。
    /// </summary>
    public void SetClickThrough(bool on)
    {
        long ex = Win32Overlay.GetWindowLongPtr(Handle, Win32Overlay.GWL_EXSTYLE).ToInt64();
        if (on) ex |= Win32Overlay.WS_EX_TRANSPARENT;
        else ex &= ~(long)Win32Overlay.WS_EX_TRANSPARENT;
        Win32Overlay.SetWindowLongPtr(Handle, Win32Overlay.GWL_EXSTYLE, new IntPtr(ex));

        if (on)
        {
            _punchEnabled = false;
            Win32Overlay.SetWindowRgn(Handle, IntPtr.Zero, true);
        }
        RefreshFrame();
    }

    /// <summary>样式位改完要让 DWM 重算一遍非客户区。</summary>
    private void RefreshFrame()
        => Win32Overlay.SetWindowPos(Handle, Win32Overlay.HWND_TOPMOST, 0, 0, 0, 0,
            Win32Overlay.SWP_NOMOVE | Win32Overlay.SWP_NOSIZE
            | Win32Overlay.SWP_NOACTIVATE | Win32Overlay.SWP_FRAMECHANGED);
}
