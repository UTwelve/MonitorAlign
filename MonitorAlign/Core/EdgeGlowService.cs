using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MonitorAlign.Native;
using MonitorAlign.Overlay;

namespace MonitorAlign.Core;

// EdgeGlowConfig 已拆到 Core/EdgeGlowConfig.cs（WPF / WinUI 两版共用同一份定义）。

/// <summary>
/// 边缘泛光服务：低频轮询全局鼠标位置，光标接近某条接缝时，
/// 让那道接缝的光源覆盖层亮起来（快进慢出，离开即柔灭）。
///
/// 挂在 App 级而不是主窗口上：主窗口关到托盘后泛光继续有效。
/// 用 30fps 轮询而不是全局鼠标钩子：没有注入风险、不占钩子链，
/// 对"轻微泛光"这种氛围反馈完全够顺滑。
/// </summary>
public sealed class EdgeGlowService
{
    /// <summary>基准起辉距离（逻辑像素），实际 = 120 ÷ 灵敏度。</summary>
    private const double BaseNearPx = 120;

    private readonly List<(EdgeGlowOverlay Win, Seam Seam, double Cur)> _items = new();
    private DispatcherTimer _timer;
    private IReadOnlyList<Seam> _lastSeams = Array.Empty<Seam>();

    /// <summary>可调参数（设置页直接改，改完调 <see cref="ApplyConfig"/>）。</summary>
    public EdgeGlowConfig Config { get; } = new();

    public bool Enabled => _timer != null;

    /// <summary>排列/拓扑变了就重建覆盖层。未启用时只清不建。</summary>
    public void UpdateSeams(IReadOnlyList<Seam> seams)
    {
        _lastSeams = seams ?? Array.Empty<Seam>();
        Rebuild();
    }

    /// <summary>参数变化：灵敏度/亮度立即生效；照射距离/颜色需要重建窗口。</summary>
    public void ApplyConfig(bool layoutChanged)
    {
        if (!Enabled) return;
        if (layoutChanged) Rebuild();
        else foreach (var (win, _, _) in _items) win.SetBrightness(Config.Brightness);
    }

    /// <summary>主题切换后调用：默认强调色变了，重建取新色。</summary>
    public void RefreshColors() { if (Enabled) Rebuild(); }

    public void Start(IReadOnlyList<Seam> seams)
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += Tick;
            _timer.Start();
        }
        UpdateSeams(seams);
        Diag.Log($"边缘泛光已启用：{_items.Count} 条接缝");
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        foreach (var (win, _, _) in _items) win.Close();
        _items.Clear();
        Diag.Log("边缘泛光已停用");
    }

    private void Rebuild()
    {
        foreach (var (win, _, _) in _items) win.Close();
        _items.Clear();

        if (!Enabled) return;
        foreach (var s in _lastSeams)
        {
            if (s.Unreachable) continue;           // 孤屏没有"交界处"
            if (s.OverlapEnd - s.OverlapStart < 8) continue;

            var win = new EdgeGlowOverlay(s, ResolveColor(s), Config.Distance)
            {
                // 先 Show 再交给 SetIntensity 管可见性：Hide 的窗口改 Opacity 无所谓，
                // 但从未 Show 过的窗口第一次 SetIntensity(true) 时才上屏，避免启动闪一下
            };
            win.SetBrightness(Config.Brightness);
            _items.Add((win, s, 0));
        }
    }

    private Color ResolveColor(Seam s)
    {
        if (Config.Colors.TryGetValue(SeamKey(s), out string hex) &&
            TryParseHex(hex, out Color c))
            return c;

        // 缺省 = 主题强调色（Ma 画刷随明暗换肤）
        return (Application.Current.TryFindResource("MaMarkerBrush") as SolidColorBrush)?.Color
               ?? Color.FromRgb(0x4C, 0xC2, 0xFF);
    }

    public static string SeamKey(Seam s) => $"{s.Base.Index}->{s.Target.Index}";

    public static bool TryParseHex(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6) return false;
        try
        {
            color = Color.FromRgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
            return true;
        }
        catch { return false; }
    }

    private void Tick(object sender, EventArgs e)
    {
        // --glowtest：固定强度常亮，不看鼠标（截图验收用）
        if (App.GlowTestIntensity >= 0)
        {
            foreach (var (win, _, _) in _items) win.SetIntensity(App.GlowTestIntensity);
            return;
        }

        if (!Win32.GetCursorPos(out var p)) return;

        double near = Math.Clamp(BaseNearPx / Math.Max(Config.Sensitivity, 0.1), 24, 600);

        for (int i = 0; i < _items.Count; i++)
        {
            var (win, s, cur) = _items[i];

            // 光标到接缝"线段"（不是无限长直线）的距离
            double across, along;
            if (s.Vertical)
            {
                across = Math.Abs(p.X - s.SeamCoord);
                along = p.Y < s.OverlapStart ? s.OverlapStart - p.Y
                      : p.Y > s.OverlapEnd ? p.Y - s.OverlapEnd : 0;
            }
            else
            {
                across = Math.Abs(p.Y - s.SeamCoord);
                along = p.X < s.OverlapStart ? s.OverlapStart - p.X
                      : p.X > s.OverlapEnd ? p.X - s.OverlapEnd : 0;
            }
            double dist = Math.Sqrt(across * across + along * along);

            // 越近越亮（平方缓出让"靠近感"更自然），快进慢出
            double t = Math.Clamp(1 - dist / near, 0, 1);
            t *= t;
            cur += (t - cur) * (t > cur ? 0.35 : 0.12);

            win.SetIntensity(cur);
            _items[i] = (win, s, cur);
        }
    }
}
