using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MonitorAlign.Native;

namespace MonitorAlign.Overlay;

/// <summary>
/// 全透明窗口背景材质。
///
/// WinUI 3 只有 MicaBackdrop / DesktopAcrylicBackdrop，**没有内建的 TransparentBackdrop**
/// （已核对 2.3.9 的 Microsoft.UI.Xaml.winmd：TransparentBackdrop 0 命中，
/// MicaBackdrop / DesktopAcrylicBackdrop 在；三方库 WinUIEx / DevWinUI 各有一个）。
/// 覆盖层要"完全看不见"，所以自己实现。
///
/// ⚠ 两个必踩的坑，都别改回去：
///  1. <see cref="ICompositionSupportsSystemBackdrop.SystemBackdrop"/> 要的是 **OS 合成器**的
///     <c>Windows.UI.Composition.CompositionBrush</c>，不是 WinUI 那一套
///     <c>Microsoft.UI.Composition</c>（写错表现为 CS0029「无法隐式转换」；
///     而且这个接口上并没有 Compositor 属性可以顺手拿来用）。
///  2. 构造 <c>Windows.UI.Composition.Compositor</c> 之前，当前线程必须有
///     <c>Windows.System.DispatcherQueue</c>。WinUI 3 建的是
///     <c>Microsoft.UI.Dispatching.DispatcherQueue</c>，OS 层看不到 ——
///     直接 new 会抛 "The caller must initialize DispatcherQueue on this thread"。
///     所以先 <see cref="Win32Overlay.CoreMessaging.EnsureDispatcherQueue"/>。
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    /// <summary>共享合成器（与 WinUIEx 同款做法：一个进程一个，不必每次重建）。</summary>
    private static Windows.UI.Composition.Compositor _compositor;
    private static readonly object Gate = new();

    private static Windows.UI.Composition.Compositor SharedCompositor
    {
        get
        {
            if (_compositor != null) return _compositor;
            lock (Gate)
            {
                if (_compositor == null)
                {
                    Win32Overlay.CoreMessaging.EnsureDispatcherQueue();
                    _compositor = new Windows.UI.Composition.Compositor();
                }
            }
            return _compositor;
        }
    }

    private Windows.UI.Composition.CompositionBrush _brush;

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _brush = SharedCompositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        connectedTarget.SystemBackdrop = _brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
    }
}
