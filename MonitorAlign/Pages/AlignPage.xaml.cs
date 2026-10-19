using System.Windows;
using System.Windows.Controls;

namespace MonitorAlign.Pages;

/// <summary>
/// 对齐页的容器。内容（PageHost）逻辑上属于 MainWindow，这里只做摘挂：
/// 第一次显示时把 PageHost 从主窗口根 Grid 摘下，挂成自己的 Content。
/// </summary>
public partial class AlignPage : System.Windows.Controls.Page
{
    /// <summary>实例计数（--navtest 用）：导航项配了 Required 缓存，正常应恒为 1。</summary>
    public static int InstanceCount;

    public AlignPage()
    {
        InstanceCount++;
        InitializeComponent();
        Loaded += (_, _) => AttachHost();
    }

    private void AttachHost()
    {
        if (Application.Current.MainWindow is not MainWindow mw) return;
        var host = mw.PageHost;
        if (ReferenceEquals(Content, host)) return;

        // 从上一个逻辑父容器（根 Grid / 旧 AlignPage）手里摘下来
        switch (host.Parent)
        {
            case Panel p: p.Children.Remove(host); break;
            case ContentControl cc: cc.Content = null; break;
            case Decorator d: d.Child = null; break;
        }

        host.Visibility = Visibility.Visible;
        Content = host;
    }
}
