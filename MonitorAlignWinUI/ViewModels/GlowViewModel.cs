using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MonitorAlign.Core;
using MonitorAlign.Services;
using Windows.UI;                    // Color（取色器用的就是这个 WinRT 类型，不是 XAML 类型）

namespace MonitorAlign.ViewModels;

/// <summary>接缝颜色列表里的一行（已自定义的接缝）。</summary>
public sealed class GlowSeamRow
{
    public Seam Seam { get; init; }
    public string Label { get; init; } = "";
    public string Hex { get; init; } = "";

    /// <summary>选中这一行（编辑器切到它）。</summary>
    public ICommand Select { get; init; }

    /// <summary>移除自定义色，回落到主题强调色。</summary>
    public ICommand Remove { get; init; }
}

/// <summary>下拉框里的一条可选接缝。</summary>
public sealed class SeamOption
{
    public Seam Seam { get; init; }
    public string Label { get; init; } = "";

    public override string ToString() => Label;
}

/// <summary>
/// 边缘泛光设置页。
///
/// 三个数据去向（与 WPF 版同义）：
///   ① 开关 → 启停服务；② 灵敏度/亮度/距离 → 服务配置 + 立即生效；
///   ③ 接缝颜色 → 配置里的颜色表 + 重建覆盖层。
/// 每次改动都写回存档（演示模式下 <see cref="ISettingsService.Save"/> 直接丢弃）。
/// </summary>
public sealed class GlowViewModel : INotifyPropertyChanged
{
    private readonly IEdgeGlowService _glow;
    private readonly IAlignSession _session;
    private readonly ISettingsService _settings;

    /// <summary>取色器改色的防抖定时器（拖动色盘时别每帧重建覆盖层）。</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _colorTimer;

    private bool _ready;                  // 初次填充控件时不该触发"改动"
    private Seam _selected;
    private Color _pickedColor = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);

    public event PropertyChangedEventHandler PropertyChanged;

    public GlowViewModel(IEdgeGlowService glow, IAlignSession session, ISettingsService settings)
    {
        _glow = glow;
        _session = session;
        _settings = settings;

        AddPickedCommand = new RelayCommand(AddPicked, () => CanAdd);
        UseThemeCommand = new RelayCommand(UseTheme, () => HasSelection);

        var q = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (q != null)
        {
            _colorTimer = q.CreateTimer();
            _colorTimer.Interval = TimeSpan.FromMilliseconds(250);
            _colorTimer.Tick += (_, _) => { _colorTimer.Stop(); ApplyPickedColor(); };
        }
    }

    /// <summary>把下拉框选中的接缝加进自定义列表。</summary>
    public ICommand AddPickedCommand { get; }

    /// <summary>回落到主题强调色。</summary>
    public ICommand UseThemeCommand { get; }

    // ---------------- ① 启用 ----------------

    public bool Enabled
    {
        get => _glow.Enabled;
        set
        {
            if (value == _glow.Enabled) return;
            if (value) _glow.Start(_session.Topology?.Seams ?? new List<Seam>());
            else _glow.Stop();

            _settings.Current.EdgeGlowEnabled = value;
            _settings.Save();
            OnPropertyChanged();
            Diag.Log($"边缘泛光开关 → {value}");
        }
    }

    // ---------------- ② 效果参数 ----------------

    public double Sensitivity
    {
        get => _glow.Config.Sensitivity;
        set
        {
            if (double.IsNaN(value)) return;          // NumberBox 清空会给 NaN
            double v = Math.Clamp(value, 0.25, 8);
            if (Math.Abs(v - _glow.Config.Sensitivity) < 1e-6) return;
            _glow.Config.Sensitivity = v;
            if (_ready) _glow.ApplyConfig(layoutChanged: false);
            SaveParams();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SensitivityHint));
        }
    }

    public double Brightness
    {
        get => _glow.Config.Brightness;
        set
        {
            if (double.IsNaN(value)) return;          // NumberBox 清空会给 NaN
            double v = Math.Clamp(value, 0.05, 1);
            if (Math.Abs(v - _glow.Config.Brightness) < 1e-6) return;
            _glow.Config.Brightness = v;
            if (_ready) _glow.ApplyConfig(layoutChanged: false);
            SaveParams();
            OnPropertyChanged();
            OnPropertyChanged(nameof(BrightnessText));
        }
    }

    public double Distance
    {
        get => _glow.Config.Distance;
        set
        {
            if (double.IsNaN(value)) return;          // NumberBox 清空会给 NaN
            // 距离不再卡 24..320：用户要能手填超范围的值。
            // 只留一个"别把窗口搞成万像素"的防异常卡口（20000px 单侧 = 窗口 40000px）。
            DistanceCapped = value > MaxDistance;
            double v = Math.Clamp(value, MinDistance, MaxDistance);
            if (Math.Abs(v - _glow.Config.Distance) < 1e-6) return;
            _glow.Config.Distance = v;
            // 距离改的是窗口尺寸，必须重建
            if (_ready) _glow.ApplyConfig(layoutChanged: true);
            SaveParams();
            OnPropertyChanged();
        }
    }

    /// <summary>距离手填的防异常上界（不是"功能上限"，正常用不到）。</summary>
    public const double MinDistance = 8;
    public const double MaxDistance = 20000;

    /// <summary>用户填了超过安全上界的值（界面上给一句提示）。</summary>
    private bool _distanceCapped;

    public bool DistanceCapped
    {
        get => _distanceCapped;
        set
        {
            if (_distanceCapped == value) return;
            _distanceCapped = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 尾部白度（「加性强度」）0..1。
    /// 1 = 光晕尾部也是白的 —— 任意背景上都只会**变亮**，这是 source-over 下
    /// 最接近真 ADD 的行为；0 = 保留纯色相，代价是白色文档上会被压暗约 15%。
    /// 芯部恒为白热，不受这条影响。
    /// </summary>
    public double TailWhiteness
    {
        get => _glow.Config.TailWhiteness;
        set
        {
            if (double.IsNaN(value)) return;
            double v = Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _glow.Config.TailWhiteness) < 1e-6) return;
            _glow.Config.TailWhiteness = v;
            if (_ready) _glow.ApplyConfig(layoutChanged: false);
            SaveParams();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TailWhitenessText));
        }
    }

    public string TailWhitenessText => $"{Math.Round(TailWhiteness * 100)}%";

    public string SensitivityHint => Loc.Fmt(Loc.GlowSensitivityHint, Sensitivity.ToString("0.##"));
    public string BrightnessText => $"{Math.Round(Brightness * 100)}%";

    // ---------------- ③ 接缝颜色 ----------------

    public ObservableCollection<GlowSeamRow> Customized { get; } = new();
    public ObservableCollection<SeamOption> Pickable { get; } = new();

    private SeamOption _picked;
    public SeamOption Picked
    {
        get => _picked;
        set
        {
            // ⚠ 必须挡掉"同一个值"，否则这里就是一个栈溢出环：
            //   set_Picked → 通知 → x:Bind 把值写回 ComboBox
            //   → ComboBox 的 SelectedItem 依赖属性回调 → x:Bind 再写回 set_Picked → …
            // 它**每次都给同一个实例**，所以等值判断是唯一能断开的地方。
            // 实测症状：进程直接 0xC00000FD 静默死，界面只留一个纯黑窗口，
            // 连 UnhandledException 都不走 —— 极难从现象反推，见 OnPropertyChanged 的探针。
            // （Sensitivity / Brightness / Distance / Enabled 本来就有这层守卫，唯独这里漏了。）
            if (ReferenceEquals(_picked, value)) return;
            _picked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAdd));
        }
    }

    public bool CanAdd => _picked != null;

    public bool HasSelection => _selected != null;

    public string EditorTitle => _selected == null ? "" : SeamText.Describe(_selected);

    public string EmptyHint => Customized.Count == 0 ? Loc.GlowNoCustom : "";

    /// <summary>
    /// 取色器当前颜色。类型是 <c>Windows.UI.Color</c>（不是 XAML 类型，
    /// 不违反"VM 不许碰 Microsoft.UI.Xaml.*"），正好也是 <c>ColorPicker.Color</c> 的类型。
    /// </summary>
    public Color PickedColor
    {
        get => _pickedColor;
        set
        {
            // 与 Picked 同一个道理：ColorPicker 的双向绑定会把同一个颜色反复写回来，
            // 不挡就是栈溢出（0xC00000FD，进程静默死）。Color 是值类型，== 即可。
            if (_pickedColor == value) return;
            _pickedColor = value;
            if (_editSeamKey != null) _editColor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PickedColorHex));
            ScheduleColorApply();
        }
    }

    /// <summary>当前色的 #RRGGBB，给页面上的色块按钮当文案。</summary>
    public string PickedColorHex => ToHex(_pickedColor);

    // ---- 取色弹窗的三段式：开之前记快照，确定则落，取消则还原 ----
    //
    // 取色器搬到 ContentDialog 里之后，用户在弹窗里可以随便拖，
    // 拖的时候仍然实时生效（拖色盘能直接看到辉光变色，这是要的），
    // 所以「取消」必须能把颜色**和已落库的颜色**一起还原 —— 只改 _pickedColor 不够。

    private Color _editSnapshot;
    private Color _editColor;
    private string _editCustomHex;
    private string _editSeamKey;

    public void BeginColorEdit()
    {
        _colorTimer?.Stop();
        if (_selected == null) return;
        _editSeamKey = _glow.KeyOf(_selected);
        _editSnapshot = _editColor = _pickedColor;
        _glow.Config.Colors.TryGetValue(_editSeamKey, out _editCustomHex);
    }

    public void CommitColorEdit()
    {
        _colorTimer?.Stop();
        if (_editSeamKey == null) return;
        ApplyPickedColor();
        _editSeamKey = null;
        if (_selected != null) SetPickerColor(_glow.ColorOf(_selected));
    }

    public void CancelColorEdit()
    {
        _colorTimer?.Stop();
        if (_editSeamKey == null) return;
        // 快照归属于打开时的接缝，重建或切换选择后也不能还原到另一条接缝。
        ApplyColor(_editSeamKey, _editCustomHex);
        bool sameSelection = _selected != null && _glow.KeyOf(_selected) == _editSeamKey;
        _editSeamKey = null;
        if (sameSelection) SetPickerColor(_editCustomHex == null ? _glow.ColorOf(_selected) : _editSnapshot);
        else if (_selected != null) SetPickerColor(_glow.ColorOf(_selected));
    }

    /// <summary>选中某条接缝（点列表行 / 点 + 添加）。</summary>
    public void Select(Seam s)
    {
        _selected = s;
        _colorTimer?.Stop();
        SetPickerColor(_glow.ColorOf(s));   // 取色器对齐到这条缝实际在发的颜色
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(EditorTitle));
        // 选中不改变列表内容；重建会立即清掉 ListView 的选中态。
    }

    /// <summary>把下拉框里选中的接缝加进自定义列表（先落取色器当前的色）。</summary>
    public void AddPicked()
    {
        if (_picked?.Seam == null) return;
        _colorTimer?.Stop();
        _selected = _picked.Seam;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(EditorTitle));
        ApplyColor(ToHex(_pickedColor));
    }

    /// <summary>回落到主题强调色（从自定义表移除）。</summary>
    public void UseTheme()
    {
        if (_selected != null) Remove(_selected);
    }

    private void Remove(Seam seam)
    {
        _colorTimer?.Stop();
        if (_editSeamKey == _glow.KeyOf(seam)) _editSeamKey = null;
        _selected = seam;
        ApplyColor(null);
        // 只同步实际主题色，不能经由 setter 再排入一次自定义色写入。
        SetPickerColor(_glow.ColorOf(seam));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(EditorTitle));
    }

    /// <summary>落一种颜色。hex 为 null = 用主题强调色（从自定义表移除）。</summary>
    public void ApplyColor(string hex)
    {
        if (_selected != null) ApplyColor(_glow.KeyOf(_selected), hex);
    }

    private void ApplyColor(string key, string hex)
    {
        if (hex == null) _glow.Config.Colors.Remove(key);
        else _glow.Config.Colors[key] = hex.ToUpperInvariant();

        _settings.Current.EdgeGlowColors = new Dictionary<string, string>(_glow.Config.Colors);
        _settings.Save();

        _glow.RefreshColors();            // 重建覆盖层取新色（未启用时只更新配置）
        RaiseColors();
    }

    private void ApplyPickedColor()
    {
        if (_editSeamKey != null) ApplyColor(_editSeamKey, ToHex(_editColor));
        else if (_selected != null) ApplyColor(ToHex(_pickedColor));
    }

    /// <summary>调色防抖：拖色盘时别每帧重建覆盖层。</summary>
    private void ScheduleColorApply()
    {
        if (!_ready || (_selected == null && _editSeamKey == null)) return;
        if (_colorTimer == null) { ApplyPickedColor(); return; }
        _colorTimer.Stop();
        _colorTimer.Start();
    }

    /// <summary>只改字段并通知（不触发"用户调色"那条链，见 UseTheme）。</summary>
    private void SetPickerColor(Color c)
    {
        if (_pickedColor == c) return;
        _pickedColor = c;
        OnPropertyChanged(nameof(PickedColor));
        OnPropertyChanged(nameof(PickedColorHex));
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---------------- 生命周期 ----------------

    /// <summary>启动时把存档里的参数灌回服务；开着就立刻起。</summary>
    public void RestoreFromSettings()
    {
        _session.EnsureAnalyzed();

        var s = _settings.Current;
        _glow.Config.Sensitivity = Math.Clamp(s.EdgeGlowSensitivity, 0.25, 8);
        _glow.Config.Brightness = Math.Clamp(s.EdgeGlowBrightness, 0.05, 1);
        _glow.Config.Distance = Math.Clamp(s.EdgeGlowDistance, MinDistance, MaxDistance);
        _glow.Config.TailWhiteness = Math.Clamp(s.EdgeGlowTailWhiteness, 0, 1);
        _glow.Config.Colors = new Dictionary<string, string>(s.EdgeGlowColors);

        // --glow：本次启动强制开（不写存档，用户没在中途改开关）
        bool on = s.EdgeGlowEnabled || App.GlowAtStart || App.GlowTestIntensity >= 0;
        if (on) _glow.Start(_session.Topology?.Seams ?? new List<Seam>());

        _ready = true;
        Refresh();
    }

    /// <summary>进入本页：接缝清单可能变了，重新拉一遍。</summary>
    public void Activate()
    {
        _session.EnsureAnalyzed();
        _ready = true;
        Refresh();
    }

    private void Refresh()
    {
        var seams = _session.Topology?.Seams ?? new List<Seam>();
        string selectedKey = _selected == null ? null : _glow.KeyOf(_selected);
        var selected = seams.FirstOrDefault(x => !x.Unreachable && _glow.KeyOf(x) == selectedKey);
        if (selected == null && _editSeamKey == null) _colorTimer?.Stop();
        _selected = selected;
        if (_selected != null && _editSeamKey == null && _colorTimer?.IsRunning != true)
            SetPickerColor(_glow.ColorOf(_selected));

        Customized.Clear();
        foreach (var s in seams)
        {
            if (s.Unreachable || !_glow.HasCustomColor(s)) continue;
            Seam captured = s;
            Customized.Add(new GlowSeamRow
            {
                Seam = s,
                Label = SeamText.Describe(s),
                Hex = _glow.Config.Colors.TryGetValue(_glow.KeyOf(s), out string h) ? h : "",
                Select = new RelayCommand(() => Select(captured)),
                Remove = new RelayCommand(() => Remove(captured))
            });
        }

        Pickable.Clear();
        foreach (var s in seams.Where(x => !x.Unreachable && !_glow.HasCustomColor(x))
                               .OrderBy(x => x.Base.Index).ThenBy(x => x.Target.Index))
            Pickable.Add(new SeamOption { Seam = s, Label = SeamText.Describe(s) });

        Picked = Pickable.FirstOrDefault();
        OnPropertyChanged(null);
    }

    /// <summary>重建请求是否被排进队列（见 <see cref="RequestRefresh"/>）。</summary>
    private bool _refreshQueued;

    private void RaiseColors() => RequestRefresh();

    /// <summary>
    /// 把 <see cref="Refresh"/> 排到**下一个 dispatcher tick**。
    ///
    /// ⚠ 这一层延后是必须的，不是性能优化：
    /// Refresh 会 <c>Customized.Clear()</c> 把 ListView 的项全部换掉，而它的调用方
    /// 几乎总在**事件派发过程中** —— 点删除按钮、点列表行（SelectionChanged）、
    /// 点 + 添加。在派发途中把"正握着这次点击的那个 ListViewItem"销毁，
    /// WinUI 会直接崩（和拖动断线是同一类问题：元素在事件里被销毁；
    /// 区别只是拖动表现为断线，这里直接进程死掉）。
    ///
    /// 延后一个 tick，点击先派发完，再重建，二者就不再相撞。
    /// </summary>
    private void RequestRefresh()
    {
        if (_refreshQueued) return;                  // 同一 tick 内只排一次
        var q = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (q == null) { Refresh(); return; }        // 没有队列（单测/非 UI 线程）就地跑

        _refreshQueued = true;
        q.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void SaveParams()
    {
        if (!_ready) return;
        var s = _settings.Current;
        s.EdgeGlowSensitivity = _glow.Config.Sensitivity;
        s.EdgeGlowBrightness = _glow.Config.Brightness;
        s.EdgeGlowDistance = _glow.Config.Distance;
        s.EdgeGlowTailWhiteness = _glow.Config.TailWhiteness;
        _settings.Save();
    }

    /// <summary>通知的重入深度（安全网，见下）。</summary>
    private int _pcDepth;

    /// <summary>
    /// 属性变化通知。
    ///
    /// 这里带一层**重入安全网**：每次通知都会把值推给 x:Bind，双向绑定可能把值再写回来，
    /// 写回来又触发通知 —— 环一旦成立就是栈溢出，进程**静默**死（0xC00000FD），
    /// 界面只剩一个纯黑窗口，UnhandledException 都不走，从现象完全反推不出来
    /// （泛光页的 ComboBox 就这么翻过一次车）。
    /// 所以宁可在这里断开并留一条带调用栈的日志，也不要让用户看到一个不明不白的黑窗。
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string name = null)
    {
        if (_pcDepth > 12)
        {
            Diag.Log($"OnPropertyChanged({name ?? "<all>"}) 重入过深，已断开。调用栈：\n" + Environment.StackTrace);
            return;
        }

        _pcDepth++;
        try
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name is null or nameof(CanAdd) or nameof(Picked) or nameof(HasSelection))
            {
                ((RelayCommand)AddPickedCommand).RaiseCanExecuteChanged();
                ((RelayCommand)UseThemeCommand).RaiseCanExecuteChanged();
            }
        }
        finally
        {
            _pcDepth--;
        }
    }
}
