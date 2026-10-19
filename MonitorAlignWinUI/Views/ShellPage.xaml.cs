using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MonitorAlign.Views;

/// <summary>
/// Shell：NavigationView 导航 + 页面注册（winui3-skills app-shell 模式）。
/// 标尺覆盖层的显隐门控、--page 参数都走 <see cref="Navigate"/> 与 <see cref="PageChanged"/>。
/// </summary>
public sealed partial class ShellPage : Page
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        { "align",    typeof(AlignPage) },
        { "glow",     typeof(GlowPage) },
        { "dev",      typeof(DevPage) },
        { "settings", typeof(SettingsPage) },
    };

    /// <summary>当前导航目标 tag（对齐门控等用）。</summary>
    public static string CurrentTag { get; private set; } = "";

    /// <summary>导航发生（tag）。对齐页/覆盖层订阅它做显隐门控。</summary>
    public static event Action<string> PageChanged;

    /// <summary>
    /// Shell 的内容 Frame。<see cref="Page"/> 自身已有 <c>Frame</c> 属性（指向宿主），
    /// 这里不能用同名，否则 CS0108 隐藏继承成员、语义也拧。
    /// </summary>
    public Frame ShellFrame => ContentFrame;

    public ShellPage()
    {
        InitializeComponent();

        ContentFrame.Navigated += (_, e) =>
        {
            var tag = Pages.FirstOrDefault(kv => kv.Value == e.SourcePageType).Key;
            if (tag != null) SyncSelection(tag);
        };

        Loaded += (_, _) =>
        {
            if (App.MainWindow != null)
                App.MainWindow.SetTitleBar(AppTitleBar);

            // 初始页：--page 参数优先，缺省对齐页
            string initial = "align";
            if (Pages.ContainsKey(App.InitialPage)) initial = App.InitialPage;
            if (CurrentTag == null || CurrentTag.Length == 0)
                Navigate(initial);

            if (App.NavTest) StartNavTest();
        };
    }

    /// <summary>
    /// <c>--navtest</c>：自己把四个页面轮流点一遍（不需要注入鼠标，也就不必打扰用户）。
    ///
    /// 存在的理由：页面在"直接被 --page 打开"和"从别的页切过来"两条路径上，
    /// 触发的生命周期完全不同（Loaded/可视化树/双向绑定生效顺序）。曾经泛光页
    /// 在后者上稳定栈溢出（0xC00000FD）—— 那是用户真实会走的路径，却只被
    /// "--page 一张张截"这种验收漏掉了。这条钩子专门覆盖那条路径。
    ///
    /// 判据只在日志里：每条"导航：tag"。进程没死 + 四条日志齐全 = 通过。
    /// </summary>
    private void StartNavTest()
    {
        string[] seq = { "glow", "settings", "dev", "align", "glow", "align" };
        int i = 0;

        _navTestTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _navTestTimer.Interval = TimeSpan.FromMilliseconds(900);
        _navTestTimer.Tick += (_, _) =>
        {
            if (i >= seq.Length)
            {
                _navTestTimer.Stop();
                Diag.Log($"--navtest 完成：{seq.Length} 次切换全部落地，进程仍然活着");
                return;
            }
            Diag.Log($"--navtest 第 {i + 1}/{seq.Length} 次 → {seq[i]}");
            Navigate(seq[i]);
            i++;
        };
        _navTestTimer.Start();
        Diag.Log("--navtest 开始：每 900ms 换一页");
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer _navTestTimer;

    /// <summary>导航到 tag（程序化与导航项点击共用入口）。已在目标页则只同步选中态。</summary>
    public void Navigate(string tag)
    {
        if (!Pages.TryGetValue(tag, out var type))
        {
            Diag.Log($"未知导航目标：{tag}");
            return;
        }

        if (ContentFrame.CurrentSourcePageType != type)
        {
            try
            {
                ContentFrame.Navigate(type);
            }
            catch (Exception ex)
            {
                // 页面构造 / XAML 解析失败会从这里出去。不打日志的话，表现只是
                // "窗口一片黑、什么都没有"，完全无从下手（泛光页踩过一次）。
                Diag.Log($"导航失败 {tag}：{ex.GetType().Name}: {ex.Message}\n{ex}");
                throw;
            }
        }
        else
            SyncSelection(tag);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Navigate(tag);
            if (sender.DisplayMode != NavigationViewDisplayMode.Expanded)
                sender.IsPaneOpen = false;
        }
    }

    /// <summary>选中项与全局 tag 同步。</summary>
    private void SyncSelection(string tag)
    {
        CurrentTag = tag;
        var all = NavView.MenuItems.OfType<NavigationViewItem>()
            .Concat(NavView.FooterMenuItems.OfType<NavigationViewItem>());
        NavView.SelectedItem = all.FirstOrDefault(i => (string)i.Tag == tag);
        PageChanged?.Invoke(tag);
        Diag.Log($"导航：{tag}");
    }
}
