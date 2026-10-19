using System.Globalization;
using System.Reflection;

namespace MonitorAlign;

/// <summary>
/// 极简本地化：按系统 UI 语言在「中文 / 英文」之间切换，缺省英文。
///
/// 为什么不用 .resx + 卫星程序集：
///   本项目是单文件自包含发布，且 csproj 里限制了 SatelliteResourceLanguages，
///   卫星程序集不会打进 exe，中文资源会整包丢失。这里改用进程内静态词条，
///   零依赖、单文件必然可用。
///
/// 语言在 App.OnStartup 里（创建任何窗口之前）一次性确定，
/// 因此 XAML 用 {x:Static loc:Loc.Xxx} 直接绑定即可，无需动态刷新。
/// </summary>
public static class Loc
{
    /// <summary>"en" 或 "zh"。</summary>
    public static string Lang { get; private set; } = "en";

    public static bool Zh => Lang == "zh";

    /// <summary>
    /// 选择语言。forced 非空时强制指定（--lang 参数用）；
    /// 否则只认中文（zh*），其它任何语言一律回落英文。
    /// </summary>
    public static void Init(string forced = "")
    {
        if (!string.IsNullOrWhiteSpace(forced))
        {
            string f = forced.Trim();
            Lang = f.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
        }
        else
        {
            string n = CultureInfo.CurrentUICulture.Name;
            Lang = n.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
        }

        try
        {
            var ci = new CultureInfo(Lang == "zh" ? "zh-CN" : "en-US");
            CultureInfo.DefaultThreadCurrentUICulture = ci;
            CultureInfo.DefaultThreadCurrentCulture = ci;
            CultureInfo.CurrentCulture = ci;
            CultureInfo.CurrentUICulture = ci;
        }
        catch { /* 文化数据缺失时保持进程默认，不影响词条 */ }
    }

    private static string T(string en, string zh) => Zh ? zh : en;

    public static string Fmt(string template, params object[] args) => string.Format(template, args);

    // ==================== 关于 / 版本 ====================

    public const string AuthorUrl = "https://github.com/UTwelve";

    /// <summary>"v1.1.0"。取程序集版本的前三段，避免 XAML 里硬编码。</summary>
    public static string AppVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "v1.0.0";
            return v.Revision > 0
                ? $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
                : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "v1.0.0"; }
    }

    public static string ByAuthor => "by UTwelve";

    // ==================== 通用 ====================

    public static string WindowTitle => T("Monitor Align", "显示器对齐");
    public static string Dash => "—";
    public static string ZeroPx => "0 px";
    public static string Px(int v) => $"{(v > 0 ? "+" : v < 0 ? "-" : "")}{Math.Abs(v)} px";

    /// <summary>物理换算读数，1 逻辑像素对应多少毫米。</summary>
    public static string MmPerPixel(double mm) => $"{mm:0.##} mm/px";

    // ==================== 参考物（统一名词：对齐标尺） ====================

    /// <summary>参考物的正式名称。</summary>
    public static string Ruler => T("Align Ruler", "对齐标尺");

    /// <summary>主屏侧的标尺（对齐时的参照）。</summary>
    public static string RulerFixed => T("Align Ruler · reference", "对齐标尺 · 基准");
    /// <summary>副屏侧那个可拖的。</summary>
    public static string RulerMovable => T("Align Ruler · draggable", "对齐标尺 · 可拖动");
    /// <summary>拖拽手柄的提示（两侧通用）。</summary>
    public static string RulerDragTip => T("Drag to move the ruler along the seam", "拖动以沿接缝移动标尺");
    /// <summary>基准侧"回到居中"按钮的提示。</summary>
    public static string RulerResetTip => T("Back to the seam's center", "回到接缝居中位置");

    // ==================== 主界面 ====================

    public static string SectionTarget => T("Display layout", "显示器排列");
    public static string SectionReadout => T("Offset", "偏移读数");
    public static string PageTitle => T("Align displays", "对齐显示器");
    public static string MetricSeamError => T("Seam offset", "跨屏落差");
    public static string MetricApplied => T("Applied offset", "已应用偏移");
    public static string BtnUndoAll => T("Undo changes", "撤销修改");
    public static string TooltipUndoAll => T("Restore the arrangement from when this window opened", "恢复到本窗口打开时的排列");

    /// <summary>
    /// ToggleSwitch 两侧的文字。**必须显式给**：WinUI 的 ToggleSwitch 自带
    /// OnContent/OffContent 默认值，而它跟的是**系统 UI 语言**，不跟本程序的
    /// <see cref="Lang"/> —— 英文界面在中文系统上会出现"关"（实拍踩到）。
    /// </summary>
    public static string ToggleOn => T("On", "开");
    public static string ToggleOff => T("Off", "关");

    public static string ThemeLabel => T("Theme", "主题");
    public static string ThemeAuto => T("System", "跟随系统");
    public static string ThemeLight => T("Light", "浅色");
    public static string ThemeDark => T("Dark", "深色");
    public static string TooltipTheme => T("Theme: {0} (click to switch)", "主题：{0}（点击切换）");

    public static string TooltipOpenGithub => T("Open the author's GitHub profile", "打开作者的 GitHub 主页");

    // ==================== 导航栏 ====================

    public static string NavAlign => T("Display alignment", "显示器对齐");
    public static string NavEdgeGlow => T("Edge glow", "边缘泛光");
    public static string EdgeGlowDesc => T(
        "When the cursor nears the boundary between two displays, a soft glow rises along the boundary — spanning exactly the part where the two displays meet, so you can feel where each display is without opening settings.",
        "鼠标靠近两块屏幕的交界处时，交界处会泛起一层轻微的辉光，辉光范围正好对应两块屏幕相接的部分——不看设置也能感知每块屏幕的边界在哪。");
    public static string EdgeGlowToggle => T("Enable edge glow", "启用边缘泛光");
    public static string NavDev2 => T("Extensions", "扩展功能");
    public static string DevPlaceholder => T("Under development", "开发中");

    // ==================== 扩展功能 · 接缝标尺 → 目标分辨率 ====================

    /// <summary>接缝标尺上的两句话（讲清楚哪枚能拖）。</summary>
    public static string GaugeBaseHint => T("Reference", "基准");
    public static string GaugeDragHint => T("Drag to align", "拖动对齐");
    public static string GaugeOffsetFmt => T("{0} px", "{0} px");

    public static string DevDesc => T(
        "Extra tools. Nothing here changes a system setting — everything is measured or computed locally.",
        "额外的工具。这里不会改动任何系统设置——全部是本地测量或计算。");

    public static string DevSeamSection => T("Seam", "接缝");
    public static string DevPickSeam => T("Pick a seam", "选择接缝");
    public static string DevPlace => T("Place rulers", "放置标尺");
    public static string DevClear => T("Remove rulers", "移除标尺");
    public static string DevReset => T("Centre the rulers", "标尺归零");
    public static string DevPlaced => T("Rulers are on screen", "标尺已放到屏幕上");

    public static string DevHowTo => T(
        "Two pairs of rulers appear — one pair near each end of the seam. The solid white one sits on "
        + "the reference display and never moves; the blue one sits on the secondary display. "
        + "Look at the two screens and drag each blue ruler until each pair reads as one straight line. "
        + "Then read the result below.",
        "接缝两端各出现一对标尺。白色那枚在基准屏上、固定不动；蓝色那枚在副屏上。"
        + "看着两块屏幕，把每一对拖成一条直线，然后看下面的结果。");

    public static string DevResultSection => T("Result", "计算结果");
    public static string DevRatioLabel => T("Pixel scale ratio", "像素尺度比");
    public static string DevRatioNote => T(
        "How the secondary display's pixels compare with the reference display's, in physical terms.",
        "副屏的像素相对基准屏在物理上的尺度。");
    public static string DevTheoryLabel => T("From EDID (for reference)", "EDID 理论值（仅供参考）");
    public static string DevCurrentLabel => T("Secondary display now", "副屏当前分辨率");
    public static string DevTargetLabel => T("Target resolution", "目标分辨率");
    public static string DevCopy => T("Copy", "复制");
    public static string DevCopied => T("Copied", "已复制");
    public static string DevOffsetsLabel => T("Ruler offsets", "标尺位移");

    public static string DevAxisHeight => T("heights must match", "两侧高度须一致");
    public static string DevAxisWidth => T("widths must match", "两侧宽度须一致");

    public static string DevOffsetFmt => T("top {0} px · bottom {1} px", "上 {0} px · 下 {1} px");

    public static string DevNoChange => T("No change needed", "无需修改");
    public static string DevVerdictIdle => T(
        "Pick a seam and place the rulers to begin.",
        "选一条接缝并放置标尺即可开始。");
    public static string DevVerdictUntouched => T(
        "Rulers are at their starting position — drag the two blue ones until each pair lines up.",
        "标尺还在初始位置——拖动两个蓝色标尺，让每一对连成直线。");
    public static string DevVerdictMatch => T(
        "The two displays already share the same physical pixel pitch ({0}). No custom resolution is needed.",
        "两块屏的物理像素间距本来就一致（{0}），不需要自定义分辨率。");
    public static string DevVerdictDiff => T(
        "Pixel scale ratio {0}. Run the secondary display at {1} and its pixels will line up "
        + "physically with the reference display.",
        "像素尺度比 {0}。把副屏改成 {1}，两边像素在物理上就对齐了。");
    public static string DevVerdictTheoryOff => T(
        "(Measured value differs from the EDID figure — EDID sizes are often rounded to the nearest centimetre, "
        + "so trust the rulers.)",
        "（实测与 EDID 算出的不一致——EDID 的物理尺寸常四舍五入到厘米，以标尺实测为准。）");

    /// <summary>弹窗按钮（取色对话框等）。</summary>
    public static string DlgOk => T("Done", "完成");
    public static string DlgCancel => T("Cancel", "取消");

    /// <summary>只算不改的声明，页脚常驻。</summary>
    public static string DevReadOnly => T(
        "This page only computes. It never writes a display setting.",
        "本页只做计算，不会写入任何显示设置。");

    // ==================== 设置页 ====================

    public static string NavSettings => T("Settings", "设置");
    public static string SettingsDesc => T("Theme, startup and about.", "主题、开机自启与关于信息。");
    public static string SettingsTheme => T("Theme", "主题");
    public static string SettingsAutostart => T("Launch on startup", "开机自动启动");
    public static string SettingsAutostartHint => T(
        "Starts quietly in the background with edge glow active",
        "开机后在后台静默运行，边缘泛光保持生效");
    public static string SettingsAbout => T("About", "关于");

    // ==================== 边缘泛光 · 设置 ====================

    public static string GlowSectionEffect => T("Effect", "效果");
    public static string GlowSensitivity => T("Sensitivity", "接近灵敏度");
    public static string GlowSensitivityHint => T(
        "×{0} — higher values require the pointer to be closer to the seam",
        "×{0}——倍数越高，光标需更靠近接缝才开始发亮");
    public static string GlowBrightness => T("Brightness", "发光亮度");
    public static string GlowDistance => T("Glow distance", "光晕距离");
    public static string GlowDistanceHint => T(
        "Glow distance on each side (px). Type 8–20000; values above the former 320 px limit are supported.",
        "光晕向两侧照射的距离（px），可手填 8–20000，支持超过原来的 320 px 限制。");

    public static string GlowTailWhite => T("Additive look", "加性强度");
    public static string GlowTailWhiteHint => T(
        "Higher values whiten the halo and reduce background darkening; lower values "
        + "preserve more colour. This approximates additive glow, not true additive blending.",
        "提高后光晕更白，可减轻对亮背景的压暗；降低后颜色更浓。"
        + "这是加性泛光的视觉近似，并非真正的 ADD 混合。");

    public static string GlowSeamColors => T("Seam colors", "接缝颜色");
    public static string GlowSeamColorsDesc => T(
        "Customize the glow color per seam. Seams not in the list follow the theme accent color.",
        "为接缝逐条自定义泛光颜色；列表之外的接缝跟随主题强调色。");
    public static string GlowAddSeam => T("Add a seam to customize", "添加要自定义的接缝");
    public static string GlowNoCustom => T("No custom colors yet. Tap + to pick a seam.", "还没有自定义的颜色，点 + 添加一条接缝。");
    public static string GlowNoSeamLeft => T("Every seam already has a custom color", "所有接缝都已自定义");
    public static string GlowRemoveSeam => T("Remove custom color, use theme accent", "移除自定义颜色，改用主题强调色");
    public static string GlowUseTheme => T("Theme color", "主题色");

    /// <summary>取色器（WinUI 原生 ColorPicker）的无障碍名。</summary>
    public static string GlowPickColor => T("Pick the glow color", "选择泛光颜色");
    public static string GlowHexHint => T("HEX, e.g. #4CC2FF", "十六进制，如 #4CC2FF");
    public static string GlowHexBad => T("Not a valid HEX color", "不是有效的十六进制颜色");

    // ==================== 托盘 ====================

    public static string TrayOpen => T("Open", "打开");
    public static string TrayExit => T("Exit", "退出");
    public static string TrayTooltip => "MonitorAlign";
    public static string TrayBalloonTitle => T("Still running", "仍在运行");
    public static string TrayBalloonText => T(
        "MonitorAlign stays in the tray to keep the edge glow alive. Right-click the tray icon to exit.",
        "MonitorAlign 已缩到托盘，边缘泛光继续生效。右键托盘图标可选择退出。");

    // ==================== 接缝 ====================

    public static string SeamVertical => T("vertical", "竖直");
    public static string SeamHorizontal => T("horizontal", "水平");
    public static string ComboUnreachable =>
        T("Display #{0} is not adjacent to the primary", "#{0} 与主屏不相邻");
    public static string ComboItem => T("{0} · {1} seam · depth {2}", "{0} · {1}接缝 · 深度 {2}");

    /// <summary>缩略图上点接缝点切换的提示（跟在当前接缝文字后面）。</summary>
    public static string SeamPickHint =>
        T("tap a dot on the layout to switch seams", "点图上的接缝点可切换");

    // ==================== 状态提示 ====================

    public static string StatusIdle =>
        T("Drag the ruler to align, release to apply · Esc undoes everything",
          "拖动对齐标尺，松手即生效 · Esc 撤销全部修改");
    public static string StatusApplied => T("Display #{0} moved {1}.", "已把 #{0} 移动 {1}。");
    public static string StatusUndone => T("Reverted to the arrangement from startup.",
                                           "已撤销到启动前的排列。");
    public static string StatusUndoFailed => T("Problem while undoing: ", "撤销时出现问题：");
    public static string StatusApplyFailed => T("Failed to apply: ", "应用失败：");
    public static string StatusNegativeOrigin =>
        T("Note: this pushes a display's logical origin negative; a few legacy apps may "
        + "misplace their windows.",
          "注意：本次补偿会让某台显示器的逻辑坐标变为负值，个别老程序可能出现窗口定位异常。");
    public static string StatusUnreachable =>
        T("That display is not connected to the primary. Adjust the arrangement in Display Settings first.",
          "该显示器与主屏不连通，请先在显示设置中调整排列。");
    public static string StatusNoSeam =>
        T("No alignable seam found. Check the arrangement in Display Settings.",
          "没有找到可对齐的接缝，请在显示设置中检查排列。");

    // ==================== 底层错误（会显示在状态栏） ====================

    public static string ErrQueryMode => T("Failed to read the current display mode of {0}",
                                           "读取 {0} 当前显示模式失败");
    public static string ErrCommit => T("Failed to commit the display arrangement (code {0})",
                                        "提交显示排列失败（代码 {0}）");
    public static string ErrPartial => T("{0} display(s) failed to be written, the rest were applied",
                                         "{0} 台显示器写入失败，其余已应用");

    // ==================== 演示模式 ====================

    public static string DemoDisplay => T("Demo Display {0}", "演示显示器 {0}");
    public static string TagPrimary => T(" [primary]", " [主屏]");
}
