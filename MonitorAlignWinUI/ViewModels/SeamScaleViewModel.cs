using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MonitorAlign.Core;
using MonitorAlign.Overlay;
using MonitorAlign.Services;

namespace MonitorAlign.ViewModels;

/// <summary>
/// 扩展功能页：接缝标尺 → 目标分辨率。
///
/// 数学全在 <see cref="SeamGaugeGeometry"/>（纯几何、无 UI 依赖），
/// 这里只做三件事：拉接缝清单、把位移喂给几何、把结果翻译成人话。
///
/// ⚠ 本页**只算不改**：不写任何显示设置，也不碰存档。
/// </summary>
public sealed class SeamScaleViewModel : INotifyPropertyChanged
{
    private readonly IAlignSession _session;
    private readonly ISeamGaugeService _gauges;

    /// <summary>“已复制”提示的自动隐藏定时器（同 GlowViewModel 的防抖做法）。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _copyTimer;

    private bool _ready = true;
    private double _d1, _d2;

    public SeamScaleViewModel(IAlignSession session, ISeamGaugeService gauges)
    {
        _session = session;
        _gauges = gauges;

        PlaceCommand = new RelayCommand(() => Place(), () => Picked != null);
        ClearCommand = new RelayCommand(() => { _gauges.Clear(); _d1 = _d2 = 0; RaiseAll(); }, () => Active);
        ResetCommand = new RelayCommand(() => { _gauges.ResetOffsets(); _d1 = _d2 = 0; RaiseAll(); }, () => Active);
        CopyCommand = new RelayCommand(Copy, () => Active && Picked != null);

        var q = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (q != null)
        {
            _copyTimer = q.CreateTimer();
            _copyTimer.Interval = TimeSpan.FromMilliseconds(2200);
            _copyTimer.Tick += (_, _) => { _copyTimer.Stop(); Copied = false; };
        }

        _gauges.OffsetsChanged += (a, b) => { _d1 = a; _d2 = b; RaiseAll(); };
        Refresh();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public ICommand PlaceCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand CopyCommand { get; }

    // ---------------------------------------------------------------- 接缝选择

    public ObservableCollection<SeamOption> Seams { get; } = new();

    private SeamOption _picked;
    public SeamOption Picked
    {
        get => _picked;
        set
        {
            // 与泛光页同一个坑：不挡等值就是 x:Bind ↔ ComboBox 的栈溢出环。
            if (ReferenceEquals(_picked, value)) return;
            _picked = value;
            _gauges.Clear();                 // 换接缝 = 旧标尺作废
            _d1 = _d2 = 0;
            OnPropertyChanged();
            RaiseAll();
        }
    }

    public bool CanPlace => _picked != null;
    public bool Active => _gauges.Active;
    public bool Idle => !Active;

    /// <summary>当前摆着标尺的那条接缝的描述。</summary>
    public string ActiveSeamText => _gauges.Current == null ? "" : SeamText.Describe(_gauges.Current);

    /// <summary>页面进入时刷新清单。</summary>
    public void Activate()
    {
        _session.EnsureAnalyzed();
        Refresh();
    }

    /// <summary>页面离开时把标尺收干净（否则屏幕上会留四个幽灵窗口）。</summary>
    public void Deactivate()
    {
        _gauges.Clear();
        _d1 = _d2 = 0;
        Copied = false;
        RaiseAll();
    }

    /// <summary>
    /// <c>--gauge d1,d2</c>：把第一条接缝的标尺摆上，并把两枚可拖标尺推到给定位移。
    ///
    /// 这是**只为自动验收**留的入口：锁屏 / 无人值守时鼠标事件根本送不到覆盖层，
    /// 「点放置 → 拖两枚」这套交互没法跑；但「位移 → 比值 → 目标分辨率」这条**计算链**
    /// 与鼠标无关，必须能独立验证。
    /// </summary>
    public void DebugPlace(double d1, double d2)
    {
        Activate();
        if (Picked == null)
        {
            Diag.Log("--gauge：没有可用的接缝，跳过");
            return;
        }
        Place();
        _gauges.SetPairOffset(0, d1);
        _gauges.SetPairOffset(1, d2);
        Diag.Log($"--gauge：已放置 {ActiveSeamText}，推位移 {d1:0} / {d2:0} → "
                 + $"比值 {RatioText}、目标 {TargetText}");
    }

    private void Refresh()
    {
        _session.EnsureAnalyzed();
        var seams = _session.Topology?.Seams ?? new System.Collections.Generic.List<Seam>();

        Seams.Clear();
        foreach (var s in seams.Where(x => !x.Unreachable))
            Seams.Add(new SeamOption { Seam = s, Label = SeamText.Describe(s) });

        if (_picked != null && seams.All(x => !ReferenceEquals(x, _picked.Seam)))
            _picked = null;
        if (_picked == null) _picked = Seams.FirstOrDefault();

        OnPropertyChanged(nameof(Picked));
        RaiseAll();
    }

    private void Place()
    {
        if (_picked?.Seam == null) return;
        _d1 = _d2 = 0;
        _gauges.Place(_picked.Seam);
        RaiseAll();
    }

    private void Copy()
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(TargetText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Copied = true;
            _copyTimer?.Stop();
            _copyTimer?.Start();
        }
        catch (Exception ex)
        {
            Diag.Log("复制目标分辨率失败：" + ex.Message);
        }
    }

    private bool _copied;
    public bool Copied
    {
        get => _copied;
        set
        {
            if (_copied == value) return;
            _copied = value;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- 结果

    /// <summary>两个位移（副屏物理像素）。</summary>
    public double OffsetFirst => _d1;
    public double OffsetSecond => _d2;

    /// <summary>两枚基准标尺的间距（基准屏物理像素）。</summary>
    public double BaseLength => _picked?.Seam == null
        ? 0 : SeamGaugeGeometry.Placement(_picked.Seam).Length;

    /// <summary>Δ = d₂ − d₁。</summary>
    public double Delta => _d2 - _d1;

    /// <summary>实测的像素尺度比 kB/kA（副屏相对基准屏）。</summary>
    public double Ratio => _picked?.Seam == null
        ? 1 : SeamGaugeGeometry.Solve(_picked.Seam, _d1, _d2).Ratio;

    /// <summary>EDID 理论比值（交叉验证用）。</summary>
    public double TheoryRatio => _picked?.Seam == null
        ? 1 : SeamGaugeGeometry.TheoreticalRatio(_picked.Seam);

    // 标尺没上屏时比值恒为 1、目标分辨率恒等于当前分辨率 —— 那是"没测"而不是"测出来一致"，
    // 所以一律显示占位符。否则用户一进页面就看到 1920×1080，会误以为已经算好了。
    public string RatioText => Active ? $"{Ratio * 100:0.##} %" : Loc.Dash;
    public string TheoryText => $"{TheoryRatio * 100:0.##} %";

    /// <summary>目标分辨率（不改系统，只给数）。</summary>
    public int TargetWidth => !Active || _picked?.Seam == null
        ? 0 : SeamGaugeGeometry.Solve(_picked.Seam, _d1, _d2).Width;
    public int TargetHeight => !Active || _picked?.Seam == null
        ? 0 : SeamGaugeGeometry.Solve(_picked.Seam, _d1, _d2).Height;

    public string TargetText => TargetWidth > 0 ? $"{TargetWidth} × {TargetHeight}" : Loc.Dash;

    /// <summary>当前副屏分辨率（对照用）。</summary>
    public string CurrentText => _picked?.Seam == null
        ? Loc.Dash
        : $"{_picked.Seam.Target.PelsWidth} × {_picked.Seam.Target.PelsHeight}";

    /// <summary>一句话结论。</summary>
    public string VerdictText
    {
        get
        {
            if (_picked?.Seam == null || !Active) return Loc.DevVerdictIdle;
            if (Math.Abs(_d1) < 1 && Math.Abs(_d2) < 1) return Loc.DevVerdictUntouched;

            double err = Math.Abs(Ratio - TheoryRatio) / Math.Max(TheoryRatio, 1e-6);
            if (Math.Abs(Ratio - 1) < 0.005)
                return Loc.Fmt(Loc.DevVerdictMatch, RatioText);

            string s = Loc.Fmt(Loc.DevVerdictDiff, RatioText, TargetText);
            if (err > 0.05)
                s += " " + Loc.Fmt(Loc.DevVerdictTheoryOff, TheoryText);
            return s;
        }
    }

    /// <summary>位移读数。</summary>
    public string OffsetText => Active
        ? Loc.Fmt(Loc.DevOffsetFmt, Fmt(_d1), Fmt(_d2))
        : Loc.Dash;

    private static string Fmt(double v) =>
        (v > 0.5 ? "+" : v < -0.5 ? "−" : "") + Math.Abs(Math.Round(v)).ToString("0");

    /// <summary>接缝轴向的提示（高度/宽度）。</summary>
    public string AxisText => _picked?.Seam == null
        ? "" : (_picked.Seam.Vertical ? Loc.DevAxisHeight : Loc.DevAxisWidth);

    /// <summary>状态条文案：没放标尺时讲怎么开始，放了就报当前是哪条接缝。</summary>
    public string InfoMessage => Active
        ? $"{Loc.DevPlaced} · {ActiveSeamText}"
        : Loc.DevVerdictIdle;

    private void RaiseAll()
    {
        if (!_ready) return;
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(Idle));
        OnPropertyChanged(nameof(CanPlace));
        OnPropertyChanged(nameof(ActiveSeamText));
        OnPropertyChanged(nameof(OffsetFirst));
        OnPropertyChanged(nameof(OffsetSecond));
        OnPropertyChanged(nameof(OffsetText));
        OnPropertyChanged(nameof(BaseLength));
        OnPropertyChanged(nameof(Delta));
        OnPropertyChanged(nameof(Ratio));
        OnPropertyChanged(nameof(RatioText));
        OnPropertyChanged(nameof(TheoryRatio));
        OnPropertyChanged(nameof(TheoryText));
        OnPropertyChanged(nameof(TargetWidth));
        OnPropertyChanged(nameof(TargetHeight));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(AxisText));
        OnPropertyChanged(nameof(InfoMessage));

        // 按钮的可用态不会自己重查 —— 必须手动喊一声，
        // 否则“移除标尺”在放好标尺后仍然是灰的。
        ((RelayCommand)PlaceCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ResetCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CopyCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 变化通知 + 重入安全网 —— 与 GlowViewModel 同一套。
    /// x:Bind 双向绑定与控件属性回调成环时是**静默栈溢出**（0xC00000FD），
    /// 从现象完全反推不出来，宁可在这里断开并留日志。
    /// </summary>
    private int _pcDepth;

    private void OnPropertyChanged([CallerMemberName] string name = null)
    {
        if (_pcDepth > 12)
        {
            Diag.Log($"SeamScaleViewModel.OnPropertyChanged({name ?? "<all>"}) 重入过深，已断开。\n"
                     + Environment.StackTrace);
            return;
        }
        _pcDepth++;
        try { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        finally { _pcDepth--; }
    }
}
