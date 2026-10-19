using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using MonitorAlign.Converters;
using MonitorAlign.Core;
using MonitorAlign.Native;
using MonitorAlign.Overlay;
using Windows.UI;

namespace MonitorAlign.Services;

/// <summary>
/// 边缘泛光服务：低频轮询全局鼠标位置，光标接近某条接缝时让那道接缝的光源覆盖层亮起来
/// （快进慢出，离开即柔灭）。
///
/// 挂在 App 级而不是主窗口上：主窗口关到托盘后泛光继续有效。
/// 用 30fps 轮询而不是全局鼠标钩子：没有注入风险、不占钩子链，
/// 对"轻微泛光"这种氛围反馈完全够顺滑。
///
/// 计时器用 WinUI 的 <c>DispatcherQueueTimer</c>（WPF 版用的是 DispatcherTimer）——
/// 它必须在有 DispatcherQueue 的线程上创建，服务也就只能在 UI 线程上起停。
/// </summary>
public sealed class EdgeGlowService : IEdgeGlowService
{
    /// <summary>基准起辉距离（逻辑像素），实际 = 120 ÷ 灵敏度。</summary>
    private const double BaseNearPx = 120;

    private readonly List<(GlowOverlay Win, Seam Seam, double Cur)> _items = new();
    private DispatcherQueueTimer _timer;
    private IReadOnlyList<Seam> _lastSeams = Array.Empty<Seam>();

    public EdgeGlowConfig Config { get; } = new();

    public bool Enabled => _timer != null;

    public void UpdateSeams(IReadOnlyList<Seam> seams)
    {
        _lastSeams = seams ?? Array.Empty<Seam>();
        Rebuild();
    }

    public void ApplyConfig(bool layoutChanged)
    {
        if (!Enabled) return;
        if (layoutChanged) Rebuild();
        else
            foreach (var (win, _, _) in _items)
            {
                win.SetBrightness(Config.Brightness);
                win.SetTailWhiteness(Config.TailWhiteness);
            }
    }

    public void RefreshColors()
    {
        if (Enabled) Rebuild();
    }

    public void Start(IReadOnlyList<Seam> seams)
    {
        if (_timer == null)
        {
            var q = DispatcherQueue.GetForCurrentThread();
            if (q == null)
            {
                Diag.Log("边缘泛光启动失败：当前线程没有 DispatcherQueue");
                return;
            }
            _timer = q.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.Tick += Tick;
            _timer.Start();
        }
        UpdateSeams(seams);
        Diag.Log($"边缘泛光已启用：{_items.Count} 条接缝");
    }

    public void Stop()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer = null;
        }
        foreach (var (win, _, _) in _items) win.Shutdown();
        _items.Clear();
        Diag.Log("边缘泛光已停用");
    }

    private void Rebuild()
    {
        foreach (var (win, _, _) in _items) win.Shutdown();
        _items.Clear();

        if (!Enabled) return;
        foreach (var s in _lastSeams)
        {
            if (s.Unreachable) continue;                        // 孤屏没有"交界处"
            if (s.OverlapEnd - s.OverlapStart < 8) continue;

            var win = new GlowOverlay(s, ResolveColor(s), Config.Distance, Config.TailWhiteness);
            win.SetBrightness(Config.Brightness);
            _items.Add((win, s, 0));
        }
    }

    /// <summary>本条接缝发什么颜色的光：自定义色优先，否则主题强调色。</summary>
    public Color ResolveColor(Seam s)
    {
        if (Config.Colors.TryGetValue(KeyOf(s), out string hex) && TryParseHex(hex, out Color c))
            return c;

        // 缺省 = 主题强调色（Ma 令牌随明暗换肤）
        if (ThemeBrushConverter.From("MaAccentBrush") is Microsoft.UI.Xaml.Media.SolidColorBrush b)
            return b.Color;
        return Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    }

    public bool HasCustomColor(Seam s) => Config.Colors.ContainsKey(KeyOf(s));

    Windows.UI.Color IEdgeGlowService.ColorOf(Seam s) => ResolveColor(s);

    public static string KeyOf(Seam s) => $"{s.Base.Index}->{s.Target.Index}";
    string IEdgeGlowService.KeyOf(Seam s) => KeyOf(s);

    public static bool TryParseHex(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6) return false;
        try
        {
            color = Color.FromArgb(255,
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
            return true;
        }
        catch { return false; }
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        // --glowtest：固定强度常亮，不看鼠标（截图验收用）
        if (App.GlowTestIntensity >= 0)
        {
            foreach (var (win, _, _) in _items) win.SetIntensity(App.GlowTestIntensity);
            return;
        }

        if (!Win32Overlay.GetCursorPos(out var p)) return;

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
