using System;
using System.Collections.Generic;
using MonitorAlign.Core;

namespace MonitorAlign.Services;

/// <summary>
/// 对齐会话（界面无关的那一半）：显示器拓扑、覆盖层标尺、写系统、撤销。
///
/// 为什么单独抽一层：winui3-skills 规定 **ViewModel 不得引用 Microsoft.UI.Xaml.***，
/// 而标尺覆盖层偏偏就是 WinUI 窗口。于是把"碰窗口、碰系统"的部分放进服务，
/// ViewModel 只通过这个接口看状态、下命令 —— 两边都能各自干净。
///
/// 与 WPF 版 <c>MainWindow</c> 里那段会话逻辑同义，只是搬了家。
/// </summary>
public interface IAlignSession
{
    /// <summary>拓扑 / 读数 / 状态任一项变了。ViewModel 接到就整体刷新。</summary>
    event Action StateChanged;

    /// <summary>两侧标尺位置变了（缩略图上那两条指示线要跟着动）。参数：(基准, 目标)。</summary>
    event Action<double, double> MarksMoved;

    IReadOnlyList<DisplayInfo> Displays { get; }
    Topology Topology { get; }
    Seam CurrentSeam { get; }
    string StatusText { get; }
    string ErrorText { get; }

    /// <summary>本次测得的跨屏落差（拖动中实时、提交后归零）。null = 还没测。</summary>
    double? MeasuredDelta { get; }

    /// <summary>相对启动基线的已应用偏移（px）。</summary>
    int AppliedOffset { get; }

    /// <summary>某屏在拓扑图里的层级（主屏 0，其余按 BFS 深度）——决定缩略图配色。</summary>
    int DepthOf(int index);

    /// <summary>当前排列是否已偏离启动基线（决定撤销按钮能不能点）。</summary>
    bool HasChanges { get; }

    double BaseMarker { get; }
    double TargetMarker { get; }

    /// <summary>显示器是否已枚举过。</summary>
    bool IsInitialized { get; }

    /// <summary>枚举显示器、读存档、分析拓扑、起标尺。整个进程只跑一次。</summary>
    void Initialize();

    /// <summary>
    /// 只保证"拓扑已分析"，**不弹标尺**。
    /// 泛光页要用接缝清单，但用户没进对齐页时不该凭空冒出标尺。
    /// </summary>
    void EnsureAnalyzed();

    /// <summary>切换要对齐的接缝（缩略图上的接缝点）。</summary>
    void SelectSeam(Seam seam);

    /// <summary>撤销到本窗口打开时的排列。</summary>
    void Undo();

    /// <summary>离开对齐页：收起标尺（别让标尺飘在别的应用上面）。</summary>
    void Suspend();

    /// <summary>回到对齐页：重新起标尺。</summary>
    void Resume();
}
