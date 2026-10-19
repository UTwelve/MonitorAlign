using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MonitorAlign.Converters;
using MonitorAlign.ViewModels;

namespace MonitorAlign.Views;

/// <summary>
/// 对齐页。
///
/// 拓扑图为什么在 code-behind 画而不是 DataTemplate：WinUI 的 <c>ItemsControl</c>
/// 会把每一项包进 <c>ContentPresenter</c>，于是模板根元素上的 <c>Canvas.Left</c>
/// 落不到容器上（UWP/WinUI 的 Setter 又不支持 Binding，没法在 ItemContainerStyle 里补）。
/// 位置是这张图的全部意义，所以这里直接用 VM 算好的坐标建形状 ——
/// **几何与业务规则全在 <see cref="AlignViewModel"/>**，这一层只做"数字 → 形状"的搬运，
/// 没有任何判断逻辑。
/// </summary>
public sealed partial class AlignPage : Page
{
    private const double ChipVisual = 9;      // 接缝点视觉直径
    private const double ChipHit = 26;        // 接缝点可点直径（比视觉大，别让用户练手）

    public AlignViewModel ViewModel { get; } =
        App.Services.GetRequiredService<AlignViewModel>();

    private bool _ready;
    private FrameworkElement _themeRoot;

    public AlignPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            _ready = true;
            ViewModel.PropertyChanged += OnViewModelChanged;
            ShellPage.PageChanged += OnPageChanged;
            App.MainWindowVisibilityChanged += OnWindowVisibilityChanged;
            _themeRoot = App.MainWindow?.Content as FrameworkElement;
            if (_themeRoot != null) _themeRoot.ActualThemeChanged += OnActualThemeChanged;
        }
        UpdateActivation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _ready = false;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ShellPage.PageChanged -= OnPageChanged;
        App.MainWindowVisibilityChanged -= OnWindowVisibilityChanged;
        if (_themeRoot != null) _themeRoot.ActualThemeChanged -= OnActualThemeChanged;
        _themeRoot = null;
        ViewModel.Deactivate();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => RenderLayout();

    /// <summary>窗口显隐和导航共用门控，非对齐页或后台形态都不得显示标尺。</summary>
    private void UpdateActivation()
    {
        if (_ready && App.MainWindowVisible && ShellPage.CurrentTag == "align")
        {
            ViewModel.Activate();
            RenderLayout();
        }
        else ViewModel.Deactivate();
    }

    private void OnWindowVisibilityChanged(bool visible) => UpdateActivation();

    private void OnPageChanged(string tag) => UpdateActivation();

    private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
    {
        // null = VM 整体刷新（RaiseAll）
        if (e.PropertyName is null or nameof(AlignViewModel.Layout)) RenderLayout();
    }

    private void TopoCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 只对宽度反应：VM 自己会改 Height，不加这个判断会来回抖
        if (e.NewSize.Width.Equals(e.PreviousSize.Width)) return;
        ViewModel.SetCanvasWidth(e.NewSize.Width);
    }

    // ---------------- 绘制 ----------------

    private void RenderLayout()
    {
        var lay = ViewModel.Layout;
        TopoCanvas.Children.Clear();
        if (lay == null) return;

        // 诊断：令牌解析失败会画成一片透明（缩略图"空了"），这里直接把结果写进日志，
        // 免得每次都靠肉眼看截图。
        Diag.Log($"拓扑重绘：{lay.Rects.Count} 屏 {lay.Chips.Count} 接缝点 高={lay.Height:0.##} " +
                 $"令牌={(ThemeBrushConverter.From("MaTopoPrimaryStrokeBrush") != null ? "已解析" : "解析失败!")}");

        // 验收用：把画布相对客户区原点、每枚接缝点的画布坐标写进日志。
        // 外面的脚本靠 ClientToScreen(hwnd,0,0) + 这两个数算出接缝点的屏幕坐标，
        // 于是"点接缝点"这件事可以做到不看截图也能自动验。
        if (Diag.Enabled)
        {
            var off = TopoCanvas.TransformToVisual(App.MainWindow?.Content)
                          ?.TransformPoint(new Windows.Foundation.Point(0, 0)) ?? default;
            Diag.Log($"拓扑画布：客户区偏移=({off.X:0.#},{off.Y:0.#}) " +
                     $"尺寸=({TopoCanvas.ActualWidth:0.#}x{lay.Height:0.#}) " +
                     $"当前={ViewModel.CurrentSeamKey}");
            foreach (var c in lay.Chips)
                Diag.Log($"  接缝点[{c.Tooltip}] @({c.Cx:0.#},{c.Cy:0.#})");
        }

        foreach (var r in lay.Rects)
        {
            var rect = new Rectangle
            {
                Width = r.W, Height = r.H,
                RadiusX = 2, RadiusY = 2,
                StrokeThickness = 1,
                Fill = ThemeBrushConverter.From(r.FillToken),
                Stroke = ThemeBrushConverter.From(r.StrokeToken)
            };
            Place(rect, r.X, r.Y, 1);
            TopoCanvas.Children.Add(rect);

            if (!r.ShowIndex) continue;
            var label = new TextBlock
            {
                Text = r.Index.ToString(),
                FontSize = 10,
                Width = r.W,
                TextAlignment = TextAlignment.Center,
                Foreground = ThemeBrushConverter.From(r.StrokeToken)
            };
            Place(label, r.LabelX, r.LabelY, 2);
            TopoCanvas.Children.Add(label);
        }

        AddMark(lay.Base);
        AddMark(lay.Target);

        foreach (var c in lay.Chips) AddChip(c);
    }

    private void AddMark(TopoMark m)
    {
        if (m == null) return;
        var rect = new Rectangle
        {
            Width = m.W, Height = m.H,
            RadiusX = 1, RadiusY = 1,
            Fill = ThemeBrushConverter.From(m.FillToken)
        };
        Place(rect, m.X, m.Y, 4);
        TopoCanvas.Children.Add(rect);
    }

    private void AddChip(TopoChip c)
    {
        var visual = new Ellipse
        {
            Width = ChipVisual, Height = ChipVisual,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeBrushConverter.From(c.FillToken),
            Stroke = ThemeBrushConverter.From(c.StrokeToken)
        };
        if (c.Dashed) visual.StrokeDashArray = new DoubleCollection { 2, 2 };

        // 命中区比视觉点大一圈；Background 必须是 Transparent 而不是 null ——
        // null 背景不吃指针事件，点了没反应。
        var hit = new Border
        {
            Width = ChipHit, Height = ChipHit,
            Background = new SolidColorBrush(Colors.Transparent),
            Child = visual
        };
        hit.Tapped += (_, _) =>
        {
            Diag.Log($"接缝点被点：{c.Tooltip}");
            ViewModel.SelectSeam(c.Seam);
        };
        ToolTipService.SetToolTip(hit, c.Tooltip);
        AutomationProperties.SetName(hit, c.Tooltip);

        Place(hit, c.Cx - ChipHit / 2, c.Cy - ChipHit / 2, 6);
        TopoCanvas.Children.Add(hit);
    }

    private static void Place(UIElement el, double x, double y, int z)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        Canvas.SetZIndex(el, z);
    }
}
