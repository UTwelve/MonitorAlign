using System.Runtime.InteropServices;
using System.Windows;
using MonitorAlign.Native;

namespace MonitorAlign.Core;

/// <summary>
/// 一台显示器的全部信息。逻辑矩形单位为「虚拟桌面物理像素」（进程声明 PerMonitorV2 后
/// GetMonitorInfo / DEVMODE.dmPosition 均以物理像素表达）。
/// </summary>
public sealed class DisplayInfo
{
    public int Index;                 // 1 起的显示序号（对齐 UI 上的编号）
    public string DeviceName = "";    // \\.\DISPLAY1
    public string FriendlyName = "";  // 显示器友好名
    public string DeviceId = "";      // 唯一键（接口路径），用于配置持久化
    public bool IsPrimary;
    public IntPtr HMonitor;

    /// <summary>逻辑矩形（虚拟桌面坐标，物理像素）。</summary>
    public Rect Rect;

    public uint DpiX = 96, DpiY = 96;
    public double Scale => DpiX / 96.0;

    public int PositionX;             // DEVMODE.dmPosition.x（可写回）
    public int PositionY;             // DEVMODE.dmPosition.y
    public int PelsWidth, PelsHeight;
    public int Frequency;
    public uint Orientation;

    public int PhysicalWidthMm, PhysicalHeightMm;

    /// <summary>毫米 / 物理像素（由 EDID 物理尺寸与分辨率推得，用于 mm 读数）。</summary>
    public double MmPerPixelX => PelsWidth > 0 && PhysicalWidthMm > 0 ? PhysicalWidthMm / (double)PelsWidth : 0;
    public double MmPerPixelY => PelsHeight > 0 && PhysicalHeightMm > 0 ? PhysicalHeightMm / (double)PelsHeight : 0;

    public int Left => (int)Rect.Left;
    public int Top => (int)Rect.Top;
    public int Right => (int)Rect.Right;
    public int Bottom => (int)Rect.Bottom;
    public int Width => (int)Rect.Width;
    public int Height => (int)Rect.Height;

    public override string ToString() =>
        $"#{Index} {DeviceName} {Width}x{Height} @({Left},{Top}) scale={Scale:0.##}{(IsPrimary ? Loc.TagPrimary : "")}";
}

public static class DisplayEnumerator
{
    /// <summary>枚举当前系统所有显示器。</summary>
    public static List<DisplayInfo> Enumerate()
    {
        var list = new List<DisplayInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref Win32.RECT rc, IntPtr data)
        {
            var mi = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(hMonitor, ref mi))
                return true;

            var info = new DisplayInfo
            {
                HMonitor = hMonitor,
                DeviceName = mi.szDevice ?? "",
                IsPrimary = (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                Rect = new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top,
                                mi.rcMonitor.Width, mi.rcMonitor.Height),
            };

            // DEVMODE：拿 dmPosition（我们要改的字段）与分辨率
            var dm = new Win32.DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>();
            if (Win32.EnumDisplaySettingsEx(info.DeviceName, Win32.ENUM_CURRENT_SETTINGS, ref dm, 0))
            {
                info.PositionX = dm.dmPositionX;
                info.PositionY = dm.dmPositionY;
                info.PelsWidth = (int)dm.dmPelsWidth;
                info.PelsHeight = (int)dm.dmPelsHeight;
                info.Frequency = (int)dm.dmDisplayFrequency;
                info.Orientation = dm.dmDisplayOrientation;
            }

            // DPI
            if (Win32.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out uint dx, out uint dy) == 0)
            {
                info.DpiX = dx;
                info.DpiY = dy;
            }

            // 友好名 + 唯一设备 ID
            var dd = new Win32.DISPLAY_DEVICE { cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            if (Win32.EnumDisplayDevices(info.DeviceName, 0, ref dd, Win32.EDD_GET_DEVICE_INTERFACE_NAME))
            {
                info.FriendlyName = dd.DeviceString ?? "";
                info.DeviceId = dd.DeviceID ?? "";
            }
            if (string.IsNullOrEmpty(info.DeviceId))
                info.DeviceId = info.DeviceName;

            // 物理尺寸（mm）
            IntPtr hdc2 = Win32.CreateDC("DISPLAY", info.DeviceName, null, IntPtr.Zero);
            if (hdc2 != IntPtr.Zero)
            {
                info.PhysicalWidthMm = Win32.GetDeviceCaps(hdc2, Win32.HORZSIZE);
                info.PhysicalHeightMm = Win32.GetDeviceCaps(hdc2, Win32.VERTSIZE);
                Win32.DeleteDC(hdc2);
            }

            list.Add(info);
            return true;
        }

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        // 排序：主屏优先，其次按 x 再按 y，保证编号稳定
        list.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            if (a.Left != b.Left) return a.Left.CompareTo(b.Left);
            return a.Top.CompareTo(b.Top);
        });

        for (int i = 0; i < list.Count; i++) list[i].Index = i + 1;
        return list;
    }

    /// <summary>
    /// 演示布局（--demo）：主屏 1 居中，上下左右各一屏，2 号右侧再接一屏，2 号下方再接一屏。
    /// 覆盖：深度 1 与深度 2 节点、分支、竖直/水平接缝各 3 条。
    /// </summary>
    public static List<DisplayInfo> Demo()
    {
        (int w, int h, int x, int y, bool primary, string name)[] defs =
        [
            (3840, 1080,     0,      0, true,  "DEMO-1"),   // 1 主屏
            (1080, 1920,  3840,   -540, false, "DEMO-2"),   // 2 右侧竖屏
            (1920, 1080,     0,  -1080, false, "DEMO-3"),   // 3 上方（左移，与 8 相接）
            (1920, 1080,   960,   1080, false, "DEMO-4"),   // 4 下方
            (1920, 1080, -1920,      0, false, "DEMO-5"),   // 5 左侧
            (1920, 1080,  4920,   -300, false, "DEMO-6"),   // 6 在 2 右侧
            (1920, 1080,  3840,   1380, false, "DEMO-7"),   // 7 在 2 下方
            (1920, 1080, -1920,  -1080, false, "DEMO-8"),   // 8 在 5 上方
        ];

        var list = new List<DisplayInfo>();
        for (int i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            list.Add(new DisplayInfo
            {
                Index = i + 1,
                DeviceName = d.name,
                FriendlyName = Loc.Fmt(Loc.DemoDisplay, i + 1),
                DeviceId = d.name,
                IsPrimary = d.primary,
                Rect = new Rect(d.x, d.y, d.w, d.h),
                PositionX = d.x,
                PositionY = d.y,
                PelsWidth = d.w,
                PelsHeight = d.h,
                DpiX = 96, DpiY = 96,
                PhysicalWidthMm = (int)(d.w * 0.25),   // 约 96dpi 的近似物理尺寸
                PhysicalHeightMm = (int)(d.h * 0.25),
            });
        }
        return list;
    }

    /// <summary>
    /// 「副屏在左 / 右 / 上 / 下」四种方位的测试布局（--arrange）。
    ///
    /// 与 <see cref="Demo"/> 的分工：Demo 是绝对坐标的 8 屏压力布局（含链深 2、分支、孤屏），
    /// 但坐标写死在 (0,0) 附近 —— 一旦真实桌面的原点不在那里（例如主屏换成了靠右的竖屏），
    /// 它的覆盖层就落到桌面之外，截不到图。这里改为把两台假显示器塞进真实桌面里**最大的一块屏**，
    /// 于是四个方位在实机上都能拍到。只造数据，绝不碰硬件。
    /// </summary>
    public static List<DisplayInfo> ArrangeDemo(string direction)
    {
        var real = Enumerate();
        var host = real.OrderByDescending(d => (long)d.Width * d.Height).FirstOrDefault();
        if (host == null) return ArrangeDemo(direction, 0, 0, 2560, 1440);
        return ArrangeDemo(direction, host.Left, host.Top, host.Width, host.Height);
    }

    /// <summary>同上，但显式给宿主矩形 —— 自检用固定尺寸，不依赖真实硬件。</summary>
    public static List<DisplayInfo> ArrangeDemo(string direction, int hx, int hy, int hw, int hh)
    {
        direction = (direction ?? "").ToLowerInvariant();
        bool sideBySide = direction is "left" or "right";
        int w = sideBySide ? hw / 2 : hw;
        int h = sideBySide ? hh : hh / 2;

        int px, py, sx, sy;                       // p = 主屏（基准侧），s = 副屏
        switch (direction)
        {
            case "left":  px = hx + w; py = hy;     sx = hx;     sy = hy;     break;
            case "below": px = hx;     py = hy;     sx = hx;     sy = hy + h; break;
            case "above": px = hx;     py = hy + h; sx = hx;     sy = hy;     break;
            default:      px = hx;     py = hy;     sx = hx + w; sy = hy;     break;   // right
        }

        var list = new List<DisplayInfo>();
        foreach (var (x, y, isPrimary, name) in new[]
                 {
                     (px, py, true, "ARRANGE-1"),
                     (sx, sy, false, "ARRANGE-2"),
                 })
        {
            list.Add(new DisplayInfo
            {
                DeviceName = name,
                FriendlyName = Loc.Fmt(Loc.DemoDisplay, isPrimary ? 1 : 2),
                DeviceId = name,
                IsPrimary = isPrimary,
                Rect = new Rect(x, y, w, h),
                PositionX = x,
                PositionY = y,
                PelsWidth = w,
                PelsHeight = h,
                DpiX = 96,
                DpiY = 96,
                PhysicalWidthMm = (int)(w * 0.25),
                PhysicalHeightMm = (int)(h * 0.25),
            });
        }
        for (int i = 0; i < list.Count; i++) list[i].Index = i + 1;
        return list;
    }
}
