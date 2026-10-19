using System.Collections.Generic;
using MonitorAlign.Core;

namespace MonitorAlign.Services;

/// <summary>
/// 界面设置存档的唯一持有者。
///
/// 为什么不放在某个页面里：对齐页（锚点/选中接缝）与泛光页（参数/颜色）都要写同一份文件，
/// 各持一份会互相覆盖。交给服务，大家改同一份、统一落盘。
///
/// 演示模式（<c>--demo</c>）下存档是**内存里的空档**：读不碰磁盘、写直接丢弃。
/// </summary>
public interface ISettingsService
{
    AlignSettings Current { get; }

    /// <summary>落盘。演示模式直接返回（不留痕迹）。</summary>
    void Save();

    /// <summary>作废重来（签名对不上 = 排列在别处被改过）。</summary>
    void Reset();

    /// <summary>
    /// 把存档签名对齐到当前排列。
    ///
    /// 不回填的后果：新存档的 Signature 是空串，下次启动又判"不符"→ 又重置 →
    /// 锚点与泛光设置**永远存不住**。WPF 版在 <c>RefreshSettings</c> 里顺手做了，
    /// 这里显式成一个方法，免得调用方忘。
    /// </summary>
    void StampSignature(IReadOnlyList<DisplayInfo> displays);

    /// <summary>存档里的排列签名是否还对得上当前显示器排列。</summary>
    bool SignatureMatches(IReadOnlyList<DisplayInfo> displays);
}

public sealed class SettingsService : ISettingsService
{
    private AlignSettings _current;

    /// <summary>
    /// 当前存档。**永远不为 null**：<see cref="SettingsStore.Load"/> 在"还没有存档文件"时
    /// 返回的是 null（不是空对象），直接拿去读字段就是一处空引用 ——
    /// 第一次运行必然踩到，所以这里补一份新的。
    /// </summary>
    public AlignSettings Current =>
        _current ??= App.IsDemo ? new AlignSettings() : (SettingsStore.Load() ?? new AlignSettings());

    public void Save()
    {
        if (App.IsDemo) return;
        SettingsStore.Save(Current);
    }

    public void Reset()
    {
        if (App.IsDemo) { _current = new AlignSettings(); return; }
        SettingsStore.Reset();
        _current = SettingsStore.Load() ?? new AlignSettings();
    }

    public bool SignatureMatches(IReadOnlyList<DisplayInfo> displays)
        => App.IsDemo || (Current.Signature ?? "") == SettingsStore.ComputeSignature(displays);

    public void StampSignature(IReadOnlyList<DisplayInfo> displays)
    {
        if (App.IsDemo) return;
        Current.Signature = SettingsStore.ComputeSignature(displays);
    }
}
