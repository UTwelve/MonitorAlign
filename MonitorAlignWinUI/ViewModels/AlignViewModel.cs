using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MonitorAlign.Core;
using MonitorAlign.Services;

namespace MonitorAlign.ViewModels;

// ============================================================================
// 拓扑缩略图的"纯数据"形状。
//
// 为什么是数据而不是控件：winui3-skills 规定 ViewModel 不得引用 Microsoft.UI.Xaml.*，
// 于是 VM 不能自己造 Rectangle。它只算**画布坐标 + 令牌名**，由视图层照着画。
// 令牌名走 ThemeBrushConverter 解析，明暗切换自动跟随。
// ============================================================================

/// <summary>缩略图里的一块屏幕。</summary>
public sealed class TopoRect
{
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public string FillToken { get; init; } = "";
    public string StrokeToken { get; init; } = "";
    public int Index { get; init; }
    public bool ShowIndex { get; init; }
    public double LabelX { get; init; }
    public double LabelY { get; init; }
}

/// <summary>缩略图上一枚可点的接缝点。</summary>
public sealed class TopoChip
{
    public double Cx { get; init; }
    public double Cy { get; init; }
    public string FillToken { get; init; } = "";
    public string StrokeToken { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public bool Dashed { get; init; }
    public Seam Seam { get; init; }
}

/// <summary>标尺位置指示（当前接缝上那两条短线）。</summary>
public sealed class TopoMark
{
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public string FillToken { get; init; } = "";
}

/// <summary>一次重绘所需的全部数据。</summary>
public sealed class TopoLayout
{
    public double Height { get; init; } = 84;
    public IReadOnlyList<TopoRect> Rects { get; init; } = Array.Empty<TopoRect>();
    public IReadOnlyList<TopoChip> Chips { get; init; } = Array.Empty<TopoChip>();
    public TopoMark Base { get; init; }
    public TopoMark Target { get; init; }
}

/// <summary>
/// 对齐页。只依赖 <see cref="IAlignSession"/>：拓扑、读数、撤销都从会话来，
/// 命令也只往会话下发 —— VM 自己不碰窗口、不碰系统。
/// </summary>
public sealed class AlignViewModel : INotifyPropertyChanged
{
    private readonly IAlignSession _session;

    private double _canvasWidth = 400;      // 首次布局前 ActualWidth 是 0，给个保守值顶上
    private TopoLayout _layout = new();

    public event PropertyChangedEventHandler PropertyChanged;

    public AlignViewModel(IAlignSession session)
    {
        _session = session;
        _session.StateChanged += OnStateChanged;
        _session.MarksMoved += OnMarksMoved;      // 拖动标尺时只动指示线，不必整页重算
        UndoCommand = new RelayCommand(() => _session.Undo(), () => CanUndo);
    }

    /// <summary>会话是否已起来（决定撤销按钮能不能点）。</summary>
    private bool Ready => _session.IsInitialized;

    public ICommand UndoCommand { get; }

    // ---------------- 供 x:Bind 的属性 ----------------

    public string StatusText => _session.StatusText;
    public string ErrorText => _session.ErrorText;
    public string AppliedText => Loc.Px(_session.AppliedOffset);
    public bool CanUndo => Ready && _session.HasChanges;

    /// <summary>沿接缝方向 1 逻辑像素 = 多少毫米（判断"还差 2px"肉眼能否看出）。</summary>
    public string ScaleText
    {
        get
        {
            var s = _session.CurrentSeam;
            if (s == null || s.Unreachable) return "";
            double mm = s.Vertical ? s.Target.MmPerPixelY : s.Target.MmPerPixelX;
            return mm > 0 ? Loc.MmPerPixel(mm) : "";
        }
    }

    /// <summary>接缝切换提示。只有一条接缝时没有可切换的东西，留空。</summary>
    public string SeamHint =>
        (_session.Topology?.Seams.Count ?? 0) > 1 ? Loc.SeamPickHint : "";

    public double CanvasHeight => _layout.Height;
    public TopoLayout Layout => _layout;

    /// <summary>当前接缝的键（"#1→#2"）。诊断日志与验收脚本靠它认"正对着哪条"。</summary>
    public string CurrentSeamKey => _session.CurrentSeam == null
        ? "-"
        : $"#{_session.CurrentSeam.Base.Index}→#{_session.CurrentSeam.Target.Index}";

    // ---------------- 页面生命周期 ----------------

    /// <summary>对齐页显示：首次要初始化会话（枚举显示器 + 起标尺）。</summary>
    public void Activate()
    {
        if (!_session.IsInitialized) _session.Initialize();
        else _session.Resume();
        Rebuild();
        RaiseAll();
    }

    /// <summary>离开对齐页：收起标尺，别让它飘在别的应用上面。</summary>
    public void Deactivate()
    {
        if (_session.IsInitialized) _session.Suspend();
    }

    /// <summary>缩略图接缝点被点中。</summary>
    public void SelectSeam(Seam seam) => _session.SelectSeam(seam);

    /// <summary>画布宽度变了（卡片宽度跟着窗口走）要重排。</summary>
    public void SetCanvasWidth(double width)
    {
        if (Math.Abs(width - _canvasWidth) < 0.5) return;
        _canvasWidth = width;
        Rebuild();
        RaiseAll();
    }

    // ---------------- 重算 ----------------

    private void OnStateChanged()
    {
        Rebuild();
        RaiseAll();
    }

    private void OnMarksMoved(double baseMarker, double targetMarker)
    {
        // 只换两条指示线：整页重算会把刚提交的状态行也刷一遍，没必要
        Rebuild();
        RaiseAll();
    }

    private void Rebuild()
    {
        var topo = _session.Topology;
        var displays = _session.Displays;
        if (topo == null || displays.Count == 0)
        {
            _layout = new TopoLayout();
            return;
        }

        var (l, t, w, h) = topo.Bounds();
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);

        double cw = _canvasWidth <= 1 ? 400 : _canvasWidth;

        // 高度跟着排列的宽高比走：否则"一横一竖"会被上下裁得很窄、左右留大片空白。
        double ch = Math.Clamp(cw * h / w, 56, 176);

        double pad = 3;
        double s = Math.Min((cw - pad * 2) / w, (ch - pad * 2) / h);
        double ox = pad + (cw - pad * 2 - w * s) / 2;
        double oy = pad + (ch - pad * 2 - h * s) / 2;

        var rects = new List<TopoRect>();
        foreach (var d in displays)
        {
            int depth = _session.DepthOf(d.Index);
            double rw = Math.Max(d.Width * s, 3);
            double rh = Math.Max(d.Height * s, 3);
            double rx = (d.Left - l) * s + ox;
            double ry = (d.Top - t) * s + oy;

            rects.Add(new TopoRect
            {
                X = rx, Y = ry, W = rw, H = rh,
                Index = d.Index,
                FillToken = FillTokenOf(depth),
                StrokeToken = StrokeTokenOf(depth),
                ShowIndex = rw >= 16 && rh >= 13,
                LabelX = rx,
                LabelY = ry + rh / 2 - 8
            });
        }

        var chips = new List<TopoChip>();
        foreach (var seam in topo.Seams)
        {
            // 接缝中点在虚拟桌面里的位置 → 画布坐标
            double lx = seam.Vertical ? seam.SeamCoord : seam.MidPoint;
            double ly = seam.Vertical ? seam.MidPoint : seam.SeamCoord;

            bool selected = !seam.Unreachable && ReferenceEquals(seam, _session.CurrentSeam);
            chips.Add(new TopoChip
            {
                Cx = (lx - l) * s + ox,
                Cy = (ly - t) * s + oy,
                FillToken = selected ? PrimaryStroke : Depth1Fill,
                StrokeToken = selected ? PrimaryStroke : Depth1Stroke,
                Tooltip = SeamText.Describe(seam),
                Dashed = seam.Unreachable,
                Seam = seam
            });
        }

        _layout = new TopoLayout
        {
            Height = ch,
            Rects = rects,
            Chips = chips,
            Base = BuildMark(PrimaryStroke, true, l, t, s, ox, oy),
            Target = BuildMark(Depth1Stroke, false, l, t, s, ox, oy)
        };
    }

    private TopoMark BuildMark(string token, bool isBase, double l, double t,
                               double s, double ox, double oy)
    {
        var seam = _session.CurrentSeam;
        if (seam == null || seam.Unreachable) return null;

        double mb = _session.BaseMarker;
        double mt = _session.TargetMarker;

        if (seam.Vertical)
        {
            double sx = (seam.SeamCoord - l) * s + ox;
            double m = (isBase ? mb : mt) - t;
            return new TopoMark
            {
                X = sx + (isBase ? -9 : 2), Y = m * s + oy - 1,
                W = 7, H = 2, FillToken = token
            };
        }
        else
        {
            double sy = (seam.SeamCoord - t) * s + oy;
            double m = (isBase ? mb : mt) - l;
            return new TopoMark
            {
                X = m * s + ox - 1, Y = sy + (isBase ? -9 : 2),
                W = 2, H = 7, FillToken = token
            };
        }
    }

    // ---------------- 令牌 ----------------

    private const string PrimaryFill = "MaTopoPrimaryFillBrush";
    private const string PrimaryStroke = "MaTopoPrimaryStrokeBrush";
    private const string Depth1Fill = "MaTopoDepth1FillBrush";
    private const string Depth1Stroke = "MaTopoDepth1StrokeBrush";
    private const string Depth2Fill = "MaTopoDepth2FillBrush";
    private const string Depth2Stroke = "MaTopoDepth2StrokeBrush";

    private static string FillTokenOf(int depth) =>
        depth == 0 ? PrimaryFill : depth == 1 ? Depth1Fill : Depth2Fill;

    private static string StrokeTokenOf(int depth) =>
        depth == 0 ? PrimaryStroke : depth == 1 ? Depth1Stroke : Depth2Stroke;

    // ---------------- 通知 ----------------

    private void RaiseAll()
    {
        // 撤销可用态跟着 HasChanges 走，按钮靠 CanExecuteChanged 刷新
        ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(null);        // null = 全部绑定重取
    }

    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
