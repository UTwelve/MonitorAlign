using System.Runtime.InteropServices;
using MonitorAlign.Native;

namespace MonitorAlign.Core;

/// <summary>某台显示器的一次位置快照。</summary>
public sealed record MonitorPosition(string DeviceName, int X, int Y);

public static class DisplayArrangement
{
    /// <summary>记录当前所有显示器的逻辑位置（用于撤销）。</summary>
    public static List<MonitorPosition> Snapshot(IEnumerable<DisplayInfo> displays) =>
        displays.Select(d => new MonitorPosition(d.DeviceName, d.PositionX, d.PositionY)).ToList();

    // 实例级依赖用于纯内存事务测试，不替换进程全局的显示 API。
    internal interface IDisplaySettings
    {
        bool TryGetCurrentMode(string deviceName, out Win32.DEVMODE mode);
        int StageMode(string deviceName, Win32.DEVMODE mode);
        int Commit();
        IReadOnlyList<DisplayInfo> Enumerate();
    }

    private sealed class NativeDisplaySettings : IDisplaySettings
    {
        public bool TryGetCurrentMode(string deviceName, out Win32.DEVMODE mode)
        {
            mode = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
            return Win32.EnumDisplaySettingsEx(deviceName, Win32.ENUM_CURRENT_SETTINGS, ref mode, 0);
        }

        public int StageMode(string deviceName, Win32.DEVMODE mode) =>
            Win32.ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero,
                Win32.CDS_UPDATEREGISTRY | Win32.CDS_NORESET, IntPtr.Zero);

        public int Commit() =>
            Win32.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

        public IReadOnlyList<DisplayInfo> Enumerate() => DisplayEnumerator.Enumerate();
    }

    /// <summary>
    /// 先读取全部原模式，再仅暂存有变化的位置，最后统一提交。
    /// 暂存或提交失败时尽力恢复原模式；只有成功后才把目标位置写入内存。
    /// </summary>
    /// <param name="dryRun">演示模式下只改内存、不动系统。</param>
    public static bool Apply(IEnumerable<DisplayInfo> displays,
                             IEnumerable<MonitorPosition> positions,
                             bool dryRun,
                             out string error) =>
        Apply(displays, positions, dryRun, new NativeDisplaySettings(), out error);

    internal static bool Apply(IEnumerable<DisplayInfo> displays,
                               IEnumerable<MonitorPosition> positions,
                               bool dryRun,
                               IDisplaySettings settings,
                               out string error)
    {
        error = null;
        var displayList = displays.ToList();
        var map = positions.ToDictionary(p => p.DeviceName, StringComparer.OrdinalIgnoreCase);

        if (dryRun)
        {
            foreach (var display in displayList)
                if (map.TryGetValue(display.DeviceName, out var position))
                    Move(display, position.X, position.Y);
            return true;
        }

        var prepared = new List<(DisplayInfo Display, Win32.DEVMODE Original, MonitorPosition Position)>();
        foreach (var display in displayList)
        {
            if (!settings.TryGetCurrentMode(display.DeviceName, out var mode))
            {
                error = Loc.Fmt(Loc.ErrQueryMode, display.DeviceName);
                Synchronize(displayList, settings);
                return false;
            }
            map.TryGetValue(display.DeviceName, out var position);
            prepared.Add((display, mode, position));
        }

        var attempted = new List<(string DeviceName, Win32.DEVMODE Original)>();
        foreach (var item in prepared)
        {
            var position = item.Position;
            if (position == null ||
                (position.X == item.Original.dmPositionX && position.Y == item.Original.dmPositionY))
                continue;

            var desired = item.Original;
            desired.dmFields = Win32.DM_POSITION;
            desired.dmPositionX = position.X;
            desired.dmPositionY = position.Y;
            // 失败调用也可能留下驱动/注册表副作用，因此同样纳入恢复范围。
            attempted.Add((item.Display.DeviceName, item.Original));
            int rc = settings.StageMode(item.Display.DeviceName, desired);
            if (rc == Win32.DISP_CHANGE_SUCCESSFUL) continue;

            error = Loc.Zh
                ? $"暂存显示器 {item.Display.DeviceName} 的排列失败（代码 {rc}）"
                : $"Failed to stage the arrangement for {item.Display.DeviceName} (code {rc})";
            error += Rollback(attempted, settings);
            Synchronize(displayList, settings);
            return false;
        }

        if (attempted.Count > 0)
        {
            int rc = settings.Commit();
            if (rc != Win32.DISP_CHANGE_SUCCESSFUL)
            {
                // 全局提交可能影响未暂存设备，失败时恢复全部已准备的原模式。
                var originals = prepared.Select(item => (item.Display.DeviceName, item.Original)).ToList();
                error = Loc.Fmt(Loc.ErrCommit, rc) + Rollback(originals, settings);
                Synchronize(displayList, settings);
                return false;
            }
        }

        foreach (var item in prepared)
        {
            var mode = item.Original;
            if (item.Position != null)
            {
                mode.dmPositionX = item.Position.X;
                mode.dmPositionY = item.Position.Y;
            }
            SynchronizeMode(item.Display, mode);
        }
        Synchronize(displayList, settings);
        return true;
    }

    private static string Rollback(List<(string DeviceName, Win32.DEVMODE Original)> attempted,
                                   IDisplaySettings settings)
    {
        var failures = new List<string>();
        foreach (var item in attempted)
        {
            var original = item.Original;
            original.dmFields |= Win32.DM_POSITION;
            int rc = settings.StageMode(item.DeviceName, original);
            if (rc != Win32.DISP_CHANGE_SUCCESSFUL) failures.Add($"{item.DeviceName}: {rc}");
        }
        // 先尽力覆盖目标暂存，再提交恢复；部分恢复失败时不能保证原子性，需报告并读回。
        int commit = settings.Commit();
        if (commit != Win32.DISP_CHANGE_SUCCESSFUL) failures.Add($"commit: {commit}");
        if (failures.Count == 0) return "";
        return (Loc.Zh ? "；恢复原排列未完全成功：" : "; Restoring the original arrangement was incomplete: ") +
               string.Join(", ", failures);
    }

    private static void Move(DisplayInfo display, int x, int y)
    {
        display.PositionX = x;
        display.PositionY = y;
        // Rect 是桌面矩形，Pels 是模式分辨率；仅改位置不能把后者冒充矩形尺寸。
        display.Rect = new System.Windows.Rect(x, y, display.Rect.Width, display.Rect.Height);
    }

    private static void SynchronizeMode(DisplayInfo display, Win32.DEVMODE mode)
    {
        Move(display, mode.dmPositionX, mode.dmPositionY);
        display.PelsWidth = (int)mode.dmPelsWidth;
        display.PelsHeight = (int)mode.dmPelsHeight;
        display.Frequency = (int)mode.dmDisplayFrequency;
        display.Orientation = mode.dmDisplayOrientation;
    }

    private static void Synchronize(List<DisplayInfo> displays, IDisplaySettings settings)
    {
        IReadOnlyList<DisplayInfo> current = Array.Empty<DisplayInfo>();
        try { current = settings.Enumerate(); }
        catch (Exception ex) { Diag.Log("排列提交后重新枚举失败：" + ex.Message); }

        foreach (var display in displays)
        {
            var actual = current.FirstOrDefault(d =>
                string.Equals(d.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (actual != null)
            {
                display.PositionX = actual.PositionX;
                display.PositionY = actual.PositionY;
                display.Rect = actual.Rect;
                display.PelsWidth = actual.PelsWidth;
                display.PelsHeight = actual.PelsHeight;
                display.Frequency = actual.Frequency;
                display.Orientation = actual.Orientation;
                display.HMonitor = actual.HMonitor;
                display.IsPrimary = actual.IsPrimary;
                display.DpiX = actual.DpiX;
                display.DpiY = actual.DpiY;
                continue;
            }

            // 热插拔或枚举失败时，仍尽量读回设备真实坐标；读不到就保留最后已知状态。
            try
            {
                if (settings.TryGetCurrentMode(display.DeviceName, out var mode))
                    SynchronizeMode(display, mode);
                else
                    Diag.Log("无法确认显示器当前排列：" + display.DeviceName);
            }
            catch (Exception ex) { Diag.Log("排列提交后读取模式失败：" + ex.Message); }
        }
    }

    /// <summary>
    /// 检测应用后是否出现负坐标——个别老程序在全负坐标下窗口定位会异常，需提前提示。
    /// </summary>
    public static bool WouldProduceNegativeOrigin(IEnumerable<MonitorPosition> positions) =>
        positions.Any(p => p.X < 0 || p.Y < 0);
}
