using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MonitorAlign.Core;

namespace MonitorAlign.Pages;

/// <summary>
/// 边缘泛光设置页（v1.6.0 重做）。
///
/// 三个数据去向：
///   ① 开关 → MainWindow.SetEdgeGlowEnabled（启停服务 + 写存档）；
///   ② 灵敏度/亮度/距离 → App.EdgeGlow.Config + ApplyConfig（立即生效）+ 写存档；
///   ③ 接缝颜色 → Config.Colors + RefreshColors（重建覆盖层）+ 写存档。
/// 演示模式下 Settings 为 null：参数只活在本次进程，不落盘。
/// </summary>
public partial class EdgeGlowPage : System.Windows.Controls.Page
{
    /// <summary>色板预设（接缝颜色编辑器）。</summary>
    private static readonly string[] Presets =
    {
        "#FF5C5C", "#FF9C41", "#FFD138", "#6CCB5C",
        "#2DBBA5", "#4CC2FF", "#9A7FE8", "#E56BA1",
    };

    private MainWindow _mw;
    private Seam _selectedSeam;
    private bool _ready;

    public EdgeGlowPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Fill();
    }

    private void Fill()
    {
        _mw = Window.GetWindow(this) as MainWindow;
        if (_mw == null) return;

        // 窗口是 SizeToContent：本页高度对齐到对齐页（扣除 Root 自己的上下边距 16+24=40），
        // 内容更高时自然撑开，切页时窗口不抖
        Root.MinHeight = Math.Max(_mw.PageAlignContentHeight - 40, 0);

        var c = App.EdgeGlow.Config;
        GlowToggle.IsChecked = _mw.EdgeGlowEnabled;
        NumSensitivity.Value = c.Sensitivity;
        SliderBrightness.Value = Math.Clamp(c.Brightness, 0.05, 1);
        NumDistance.Value = c.Distance;
        UpdateSensitivityHint(c.Sensitivity);
        UpdateBrightnessVal(c.Brightness);

        BuildSeamRows();
        _ready = true;
    }

    // ==================== ① 启用开关 ====================

    private void GlowToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _mw == null) return;
        _mw.SetEdgeGlowEnabled(GlowToggle.IsChecked == true);
    }

    // ==================== ② 效果参数 ====================

    private void NumSensitivity_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _mw == null) return;
        double v = Math.Clamp(NumSensitivity.Value ?? 1.0, 0.25, 8);
        App.EdgeGlow.Config.Sensitivity = v;
        UpdateSensitivityHint(v);
        App.EdgeGlow.ApplyConfig(layoutChanged: false);
        SaveParam();
    }

    private void SliderBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _mw == null) return;
        double v = Math.Clamp(e.NewValue, 0.05, 1);
        App.EdgeGlow.Config.Brightness = v;
        UpdateBrightnessVal(v);
        App.EdgeGlow.ApplyConfig(layoutChanged: false);
        SaveParam();
    }

    private void NumDistance_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _mw == null) return;
        double v = Math.Clamp(NumDistance.Value ?? 96, 24, 320);
        App.EdgeGlow.Config.Distance = v;
        App.EdgeGlow.ApplyConfig(layoutChanged: true);   // 距离改的是窗口尺寸，必须重建
        SaveParam();
    }

    private void UpdateSensitivityHint(double v)
        => TxtSensitivityHint.Text = Loc.Fmt(Loc.GlowSensitivityHint, v.ToString("0.##"));

    private void UpdateBrightnessVal(double v)
        => TxtBrightnessVal.Text = $"{Math.Round(v * 100)}%";

    /// <summary>把三个参数写回存档（演示模式没有存档，跳过）。</summary>
    private void SaveParam()
    {
        if (_mw.Settings is not { } s) return;
        var c = App.EdgeGlow.Config;
        s.EdgeGlowSensitivity = c.Sensitivity;
        s.EdgeGlowBrightness = c.Brightness;
        s.EdgeGlowDistance = c.Distance;
        _mw.SaveSettingsFile();
    }

    // ==================== ③ 接缝颜色 ====================

    /// <summary>接缝是否有自定义色（且这条接缝现在还存在）。</summary>
    private bool Customized(Seam s) => App.EdgeGlow.Config.Colors.ContainsKey(EdgeGlowService.SeamKey(s));

    /// <summary>重建"已自定义接缝"行列表 + 空态提示。</summary>
    private void BuildSeamRows()
    {
        SeamRows.Children.Clear();
        var seams = _mw?.AllSeams ?? Array.Empty<Seam>();

        int n = 0;
        foreach (var s in seams)
        {
            if (s.Unreachable || !Customized(s)) continue;

            var row = new Border
            {
                Padding = new Thickness(0, 6, 0, 6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = s,
            };
            row.MouseLeftButtonDown += Row_Click;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 色块：直接显示这个接缝现在发什么颜色的光
            bool parsed = EdgeGlowService.TryParseHex(
                App.EdgeGlow.Config.Colors[EdgeGlowService.SeamKey(s)], out Color col);
            var swatch = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(parsed ? col : Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(swatch, 0);
            grid.Children.Add(swatch);

            var label = new TextBlock
            {
                Text = MainWindow.SeamText(s),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryFindResource("TextFillColorSecondaryBrush") as Brush,
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var remove = new Wpf.Ui.Controls.Button
            {
                Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Dismiss20),
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                Padding = new Thickness(6, 2, 6, 2),
                ToolTip = Loc.GlowRemoveSeam,
                Tag = s,
            };
            remove.Click += BtnRemoveRow_Click;
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            row.Child = grid;
            HighlightRow(row);
            SeamRows.Children.Add(row);
            n++;
        }

        TxtNoCustom.Text = n == 0 ? Loc.GlowNoCustom : "";
        if (_selectedSeam != null && seams.All(x => !ReferenceEquals(x, _selectedSeam)))
            _selectedSeam = null;                        // 选中的接缝没了（排列变了）
        UpdateEditor();
    }

    /// <summary>选中态视觉：选中行文字用主色，未选中正常。</summary>
    private void HighlightRow(Border row)
    {
        if (row.Child is not Grid g || g.Children.Count < 2) return;
        bool sel = row.Tag is Seam s && ReferenceEquals(s, _selectedSeam);
        if (g.Children[1] is TextBlock tb)
            tb.Foreground = sel
                ? TryFindResource("TextFillColorPrimaryBrush") as Brush
                : TryFindResource("TextFillColorSecondaryBrush") as Brush;
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: Seam s }) return;
        _selectedSeam = s;
        foreach (var child in SeamRows.Children.OfType<Border>()) HighlightRow(child);
        UpdateEditor();
        e.Handled = true;
    }

    private void BtnRemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { Tag: Seam s }) return;
        ApplyColor(null);
        e.Handled = true;
    }

    /// <summary>"+"：弹出尚未自定义的接缝清单。</summary>
    private void BtnAddSeam_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var seams = (_mw?.AllSeams ?? Array.Empty<Seam>())
            .Where(s => !s.Unreachable && !Customized(s))
            .OrderBy(s => s.Base.Index).ThenBy(s => s.Target.Index);

        bool any = false;
        foreach (var s in seams)
        {
            any = true;
            var item = new MenuItem { Header = MainWindow.SeamText(s), Tag = s };
            item.Click += (_, _) =>
            {
                _selectedSeam = s;
                BuildSeamRows();
                UpdateEditor();
            };
            menu.Items.Add(item);
        }
        if (!any)
            menu.Items.Add(new MenuItem { Header = Loc.GlowNoSeamLeft, IsEnabled = false });

        menu.PlacementTarget = BtnAddSeam;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>编辑器（色板 + 主题色 + 十六进制）：只在选中了某条接缝时出现。</summary>
    private void UpdateEditor()
    {
        bool show = _selectedSeam != null;
        SeamEditor.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        TxtEditorTitle.Text = MainWindow.SeamText(_selectedSeam);
        PalettePanel.Children.Clear();
        foreach (string hex in Presets)
        {
            EdgeGlowService.TryParseHex(hex, out Color c);
            var b = new Border
            {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderBrush = TryFindResource("ControlStrokeColorDefaultBrush") as Brush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Tag = hex,
            };
            b.MouseLeftButtonDown += Swatch_Click;
            PalettePanel.Children.Add(b);
        }
        TxtHex.Text = App.EdgeGlow.Config.Colors.TryGetValue(
            EdgeGlowService.SeamKey(_selectedSeam), out string cur) ? cur : "";
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string hex }) ApplyColor(hex);
        e.Handled = true;
    }

    private void BtnThemeColor_Click(object sender, RoutedEventArgs e) => ApplyColor(null);

    private void BtnHexApply_Click(object sender, RoutedEventArgs e) => ApplyHex();

    private void TxtHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyHex(); e.Handled = true; }
    }

    private void ApplyHex()
    {
        string hex = TxtHex.Text?.Trim();
        if (!EdgeGlowService.TryParseHex(hex, out _))
        {
            TxtHex.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x4C, 0x4C));
            ToolTipService.SetToolTip(TxtHex, Loc.GlowHexBad);
            return;
        }
        TxtHex.ClearValue(Border.BorderBrushProperty);
        ToolTipService.SetToolTip(TxtHex, Loc.GlowHexHint);
        ApplyColor(hex.ToUpperInvariant());
    }

    /// <summary>落一种颜色。hex 为 null = 用主题强调色（从自定义表移除）。</summary>
    private void ApplyColor(string hex)
    {
        if (_selectedSeam == null) return;
        string key = EdgeGlowService.SeamKey(_selectedSeam);

        var colors = App.EdgeGlow.Config.Colors;
        if (hex == null) colors.Remove(key);
        else colors[key] = hex;

        if (_mw.Settings is { } s)
        {
            s.EdgeGlowColors = new Dictionary<string, string>(colors);
            _mw.SaveSettingsFile();
        }

        App.EdgeGlow.RefreshColors();                  // 重建覆盖层取新色（未启用时只更新配置）
        BuildSeamRows();
        UpdateEditor();
    }
}
