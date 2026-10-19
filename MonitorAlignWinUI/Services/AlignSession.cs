using System;
using System.Collections.Generic;
using System.Linq;
using MonitorAlign.Core;
using MonitorAlign.Overlay;

namespace MonitorAlign.Services;

/// <summary>
/// 对齐会话实现：拓扑 + 两屏标尺覆盖层 + 写系统 + 撤销。
///
/// 这是 WPF 版 <c>MainWindow</c> 里"对齐会话"那一大段的搬家版本，语义逐条对齐，
/// 包括几个踩过坑才定下来的规矩（改的时候别顺手简化回去）：
///   · 提交只算**自上次成功提交以来的增量**（<c>PendingDelta</c>），
///     不能用"标尺 − 实时接缝中点"：提交后排列变、中点漂移，会把已应用过的量再算一遍。
///   · 撤销后必须把**提交零点**也重置到当前锚点（不是接缝中点），否则下次提交
///     会把刚撤销的那段又写一遍。
///   · 基准侧拖的是锚点：两屏标尺一起挪，**不写系统**；只有目标侧松手才写。
/// </summary>
public sealed class AlignSession : IAlignSession
{
    private readonly List<DisplayInfo> _displays = new();
    private readonly Dictionary<int, int> _depthMap = new();

    private readonly ISettingsService _settingsSvc;

    private Topology _topology = new();
    private Seam _currentSeam;
    private List<MonitorPosition> _baseline = new();
    private bool _loaded;

    private RulerOverlay _baseOverlay;
    private RulerOverlay _targetOverlay;
    private double _anchorPrev;
    private double? _measuredDelta;
    private string _status = "";

    public AlignSession(ISettingsService settings)
    {
        _settingsSvc = settings;
    }

    public event Action StateChanged;
    public event Action<double, double> MarksMoved;

    /// <summary>显示器是否已经枚举过（泛光页要靠它判断要不要先跑一遍分析）。</summary>
    public bool IsInitialized => _loaded;

    public IReadOnlyList<DisplayInfo> Displays => _displays;
    public Topology Topology => _topology;
    public Seam CurrentSeam => _currentSeam;
    public string StatusText => _status;

    public string ErrorText => _measuredDelta.HasValue
        ? Loc.Px((int)Math.Round(_measuredDelta.Value))
        : Loc.Dash;

    public double? MeasuredDelta => _measuredDelta;
    public double BaseMarker => _baseOverlay?.Marker ?? _currentSeam?.MidPoint ?? 0;
    public double TargetMarker => _targetOverlay?.Marker ?? _currentSeam?.MidPoint ?? 0;

    public int AppliedOffset
    {
        get
        {
            var s = _currentSeam;
            if (s == null) return 0;
            var b = _baseline.FirstOrDefault(p => p.DeviceName == s.Target.DeviceName);
            if (b == null) return 0;
            return s.Vertical ? s.Target.PositionY - b.Y : s.Target.PositionX - b.X;
        }
    }

    public bool HasChanges()
    {
        foreach (var d in _displays)
        {
            var b = _baseline.FirstOrDefault(p => p.DeviceName == d.DeviceName);
            if (b == null) continue;
            if (d.PositionX != b.X || d.PositionY != b.Y) return true;
        }
        return false;
    }

    // 接口里是属性，这里用同义方法实现（HasChanges 语义上是"算一下"，不是存字段）
    bool IAlignSession.HasChanges => HasChanges();

    // ---------------- 生命周期 ----------------

    public void Initialize()
    {
        Load();
        Reanalyze(startOverlay: true);
        _status = Loc.StatusIdle;
        Diag.Log($"会话初始化：{_displays.Count} 屏 接缝 {_topology.Seams.Count} 条 " +
                 $"当前 #{_currentSeam?.Base.Index}→#{_currentSeam?.Target.Index}");
    }

    /// <summary>
    /// 只做分析、不弹标尺。泛光页要用接缝清单，但用户没进对齐页时不该凭空冒出标尺。
    /// </summary>
    public void EnsureAnalyzed()
    {
        if (!_loaded) Load();
        Reanalyze(startOverlay: false);
    }

    private void Load()
    {
        _displays.Clear();
        _displays.AddRange(App.IsDemo ? App.DemoDisplays() : DisplayEnumerator.Enumerate());
        _baseline = DisplayArrangement.Snapshot(_displays);
        _loaded = true;

        // 存档签名对不上 = 排列在别处被改过 → 锚点全部作废
        // （演示模式的存档是内存空档，Save() 不会落盘，SignatureMatches 恒真）
        if (!_settingsSvc.SignatureMatches(_displays))
        {
            Diag.Log("存档签名不符 → 重置（锚点与选中接缝作废）");
            _settingsSvc.Reset();
        }
        // 签名必须回填，否则下次启动又判不符（存档永远存不住，见 ISettingsService 的注释）
        _settingsSvc.StampSignature(_displays);
    }

    /// <summary>重新分析拓扑。排列变化、切换接缝后都要走一遍。</summary>
    private void Reanalyze(bool startOverlay)
    {
        _topology = TopologyAnalyzer.Analyze(_displays);
        BuildDepthMap();

        // 尽量保住用户原来的选择；没有就回落到存档里上次选的那条
        int keep = _currentSeam?.Target.Index ?? _settingsSvc.Current.SelectedTarget;
        _currentSeam = _topology.Seams.FirstOrDefault(s => !s.Unreachable && s.Target.Index == keep)
                       ?? _topology.Seams.FirstOrDefault(s => !s.Unreachable);

        if (startOverlay) StartAlignment();
        RaiseChanged();
    }

    private void BuildDepthMap()
    {
        _depthMap.Clear();
        if (_topology?.Primary != null) _depthMap[_topology.Primary.Index] = 0;
        foreach (var s in _topology.Seams)
            if (!s.Unreachable)
                _depthMap[s.Target.Index] = s.Depth;
    }

    /// <summary>某屏在拓扑图里的层级（主屏 0，其余按 BFS 深度）。</summary>
    public int DepthOf(int index) => _depthMap.TryGetValue(index, out int v) ? v : 0;

    // ---------------- 接缝选择 ----------------

    public void SelectSeam(Seam seam)
    {
        if (seam == null) return;

        if (seam.Unreachable)
        {
            _status = Loc.StatusUnreachable;
            RaiseChanged();
            return;
        }
        if (ReferenceEquals(seam, _currentSeam)) return;

        _currentSeam = seam;
        _measuredDelta = null;
        Diag.Log($"切换接缝：#{seam.Base.Index} → #{seam.Target.Index}");

        if (!App.IsDemo)
        {
            _settingsSvc.Current.SelectedTarget = seam.Target.Index;
            _settingsSvc.Save();
        }

        StartAlignment();       // 换接缝 = 把标尺挪到新接缝上
        RaiseChanged();
    }

    // ---------------- 对齐会话（标尺覆盖层） ----------------

    private void StartAlignment()
    {
        StopOverlay();
        _measuredDelta = null;

        var s = _currentSeam;
        if (s == null)
        {
            _status = Loc.StatusNoSeam;
            return;
        }
        if (s.Unreachable)
        {
            _status = Loc.StatusUnreachable;
            return;
        }

        _baseOverlay = new RulerOverlay(s.Base, s, true);
        _targetOverlay = new RulerOverlay(s.Target, s, false);

        // 存档里有这条接缝的锚点 → 恢复（对齐结果早已写进系统，不该再算一遍）
        if (!App.IsDemo && _settingsSvc.Current.Anchors.TryGetValue(SeamKey(s), out double saved))
        {
            _baseOverlay.SetMarkerSilent(saved);
            _targetOverlay.AdoptAnchor(saved);
            Diag.Log($"恢复锚点：{SeamKey(s)} = {saved:0}");
        }

        _targetOverlay.MarkerChanged += OnMarkerChanged;
        _targetOverlay.CommitRequested += OnCommitRequested;

        // Esc 与撤销按钮同义：撤到启动前。
        // （不再用它"结束会话" —— 没有「开始对齐」按钮了，结束了就再也起不来。）
        _targetOverlay.CloseRequested += Undo;
        _baseOverlay.CloseRequested += Undo;

        // 基准侧拖的是锚点：两屏标尺一起挪，不写系统
        _anchorPrev = _baseOverlay.Marker;
        _baseOverlay.MarkerChanged += OnAnchorChanged;
        _baseOverlay.AnchorSettled += OnAnchorSettled;
        _baseOverlay.ResetRequested += OnAnchorReset;

        _baseOverlay.Present();
        _targetOverlay.Present();

        // 覆盖层必须挂静态引用，否则被 GC 收走窗口就没了
        App.TrackOverlay(_baseOverlay);
        App.TrackOverlay(_targetOverlay);

        _status = Loc.StatusIdle;
    }

    private void StopOverlay()
    {
        if (_baseOverlay != null) { _baseOverlay.Shutdown(); _baseOverlay.Close(); App.ForgetOverlay(_baseOverlay); _baseOverlay = null; }
        if (_targetOverlay != null) { _targetOverlay.Shutdown(); _targetOverlay.Close(); App.ForgetOverlay(_targetOverlay); _targetOverlay = null; }
    }

    private static string SeamKey(Seam s) => $"{s.Base.Index}->{s.Target.Index}";

    private void OnAnchorChanged(double marker)
    {
        double delta = marker - _anchorPrev;
        _anchorPrev = marker;
        _targetOverlay?.ShiftAnchor(delta);
        RaiseMarks();
    }

    private void OnAnchorSettled(double marker)
    {
        if (App.IsDemo || _currentSeam == null) return;
        _settingsSvc.Current.Anchors[SeamKey(_currentSeam)] = marker;
        _settingsSvc.Save();
        Diag.Log($"锚点已保存：{SeamKey(_currentSeam)} = {marker:0}");
    }

    private void OnAnchorReset()
    {
        var s = _currentSeam;
        if (s == null || _baseOverlay == null) return;

        double delta = s.MidPoint - _baseOverlay.Marker;
        _baseOverlay.SetMarkerSilent(s.MidPoint);
        _anchorPrev = s.MidPoint;
        _targetOverlay?.ShiftAnchor(delta);
        Diag.Log($"锚点已重置到居中：{SeamKey(s)} = {s.MidPoint}");

        if (!App.IsDemo)
        {
            _settingsSvc.Current.Anchors.Remove(SeamKey(s));   // 居中 = 默认值，不必留档
            _settingsSvc.Save();
        }
        RaiseMarks();
    }

    private void OnMarkerChanged(double marker)
    {
        // 显示"自上次提交以来的增量"：不能用实时接缝中点（提交后中点会漂移）
        if (_targetOverlay == null) return;
        _measuredDelta = _targetOverlay.PendingDelta;
        RaiseChanged();
        RaiseMarks();
    }

    /// <summary>防抖后触发：把拖动结果换算成逻辑偏移并写入系统。</summary>
    private void OnCommitRequested(double marker)
    {
        var s = _currentSeam;
        if (s == null || s.Unreachable || _targetOverlay == null) return;

        int delta = _targetOverlay.PendingDelta;
        if (delta == 0) { RaiseChanged(); return; }

        var target = s.Target;
        var (nx, ny) = AlignmentSolver.SolvePosition(s.Vertical, target.PositionX, target.PositionY, delta);

        var positions = _displays.Select(d =>
            d.DeviceName == target.DeviceName
                ? new MonitorPosition(d.DeviceName, nx, ny)
                : new MonitorPosition(d.DeviceName, d.PositionX, d.PositionY)).ToList();

        if (DisplayArrangement.WouldProduceNegativeOrigin(positions))
            _status = Loc.StatusNegativeOrigin;

        bool ok = DisplayArrangement.Apply(_displays, positions, App.IsDemo, out string err);
        if (!ok)
        {
            _status = Loc.StatusApplyFailed + err;
            Diag.Log($"提交补偿失败：delta={delta} → ({nx},{ny})  {err}");
            RaiseChanged();
            return;
        }

        Diag.Log($"提交补偿：#{target.Index} delta={delta} → ({nx},{ny})" +
                 (App.IsDemo ? "（演示，未写系统）" : "（已写系统）"));

        // 排列由本会话修改，签名必须一起更新，否则下次启动会误判为外部修改。
        _settingsSvc.StampSignature(_displays);
        _settingsSvc.Save();

        // 记账：下次提交只算新的增量（失败时不记，重试仍按本次增量）
        _targetOverlay.MarkCommitted();
        _measuredDelta = 0;
        _status = Loc.Fmt(Loc.StatusApplied, target.Index, Loc.Px(-delta));
        RaiseChanged();
    }

    // ---------------- 撤销 ----------------

    public void Undo()
    {
        if (_baseline.Count == 0) return;

        bool ok = DisplayArrangement.Apply(_displays, _baseline, App.IsDemo, out string err);
        Diag.Log($"撤销到启动前：{(ok ? "成功" : "失败 " + err)}");
        if (!ok)
        {
            // 失败不消费尚未提交的增量，也不重建标尺冒充撤销成功。
            _status = Loc.StatusUndoFailed + err;
            RaiseChanged();
            return;
        }

        _settingsSvc.StampSignature(_displays);
        _settingsSvc.Save();

        // 排列回到起点，但标尺还停在拖过的位置 —— 必须把提交零点也重置，
        // 否则下一次提交会把"已撤销的那段"再算一遍。
        // 零点 = 当前锚点（基准标尺的位置），不是接缝中点。
        _targetOverlay?.ResetCommitBaseline(BaseMarker);

        _measuredDelta = null;
        Reanalyze(startOverlay: true);
        _status = Loc.StatusUndone;
        RaiseChanged();
    }

    // ---------------- 页面切换 ----------------

    public void Suspend() => StopOverlay();

    public void Resume()
    {
        if (_baseOverlay == null && _currentSeam != null) StartAlignment();
        RaiseChanged();
    }

    private void RaiseChanged() => StateChanged?.Invoke();
    private void RaiseMarks() => MarksMoved?.Invoke(BaseMarker, TargetMarker);
}
