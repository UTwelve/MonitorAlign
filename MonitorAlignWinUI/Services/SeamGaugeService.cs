using System;
using System.Collections.Generic;
using MonitorAlign.Core;
using MonitorAlign.Overlay;

namespace MonitorAlign.Services;

/// <summary>
/// 接缝标尺服务：在接缝两端各摆一对标尺 —— 基准屏那枚纯显示，副屏那枚可拖。
/// 一共四个小覆盖层窗口（每个标尺一个），这样命中区域天然就只有标尺带，
/// 不必在一个大窗口里打两个洞。
///
/// 与泛光层一样挂在 App 级，但只在扩展功能页打开时才存在；
/// 页面离开／窗口关闭都要 <see cref="Clear"/>，否则屏幕上会留下四个幽灵窗口。
/// </summary>
public sealed class SeamGaugeService : ISeamGaugeService
{
    /// <summary>一个标尺窗口 + 它属于哪一对（0 = 上/左，1 = 下/右）。</summary>
    private readonly List<(SeamGaugeOverlay Win, int Pair)> _items = new();

    public bool Active => _items.Count > 0;

    public Seam Current { get; private set; }

    public event Action<double, double> OffsetsChanged;

    public void Place(Seam seam)
    {
        Clear();
        if (seam == null || seam.Unreachable) return;

        var (first, second, length) = SeamGaugeGeometry.Placement(seam);
        if (length <= 0)
        {
            Diag.Log("接缝标尺：接缝太短，未放置");
            return;
        }

        Current = seam;
        try
        {
            Add(seam, seam.Base, first, 0);
            Add(seam, seam.Base, second, 1);
            Add(seam, seam.Target, first, 0);
            Add(seam, seam.Target, second, 1);
            Diag.Log($"接缝标尺已放置：{seam.Label} L={length:0.##}");
        }
        catch (Exception ex)
        {
            Diag.Log("接缝标尺放置失败：" + ex);
            Clear();
        }
    }

    private void Add(Seam seam, DisplayInfo display, double nominal, int pair)
    {
        // 基准屏与目标屏各自的那一枚：只有目标屏（副屏）那两枚可拖。
        bool movable = ReferenceEquals(display, seam.Target);
        var win = new SeamGaugeOverlay(seam, display, nominal, movable);
        if (movable) win.OffsetChanged += (_, _) => Raise();
        _items.Add((win, pair));
    }

    public void Clear()
    {
        foreach (var (win, _) in _items)
        {
            try { win.Shutdown(); } catch { /* 关窗失败不该带崩页面 */ }
        }
        _items.Clear();
        Current = null;
    }

    public void ResetOffsets()
    {
        foreach (var (win, _) in _items)
            if (win.IsMovable) win.ResetOffset();
        Raise();
    }

    public void SetPairOffset(int pair, double physical)
    {
        foreach (var (win, p) in _items)
            if (win.IsMovable && p == pair) win.SetOffset(physical);
        Raise();
    }

    private void Raise()
    {
        double d0 = 0, d1 = 0;
        foreach (var (win, pair) in _items)
        {
            if (!win.IsMovable) continue;
            if (pair == 0) d0 = win.Offset;
            else d1 = win.Offset;
        }
        OffsetsChanged?.Invoke(d0, d1);
    }

    /// <summary>取当前位移（供 VM 主动问一次）。</summary>
    public (double First, double Second) Offsets()
    {
        double d0 = 0, d1 = 0;
        foreach (var (win, pair) in _items)
        {
            if (!win.IsMovable) continue;
            if (pair == 0) d0 = win.Offset;
            else d1 = win.Offset;
        }
        return (d0, d1);
    }
}
