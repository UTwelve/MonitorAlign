using System.IO;
using System.Text;
using MonitorAlign.Overlay;
using MonitorAlign.Native;

namespace MonitorAlign.Core;

/// <summary>
/// 几何自检（--geomcheck）：把「副屏在左 / 右 / 上 / 下」四个方位从
/// 拓扑分析 → 标尺锚点 → 补偿换算整条链路走一遍并断言。
///
/// 存在的理由：这个工具的正确性有一半在屏幕上（线贴不贴接缝、标签是不是紧挨接缝），
/// 而这些在看不到界面的环境里没法确认。四方位里曾经错了三个（副屏不在左侧时基准标签
/// 漂到 180px 开外），肉眼全靠碰运气。
///
/// 两层断言：
///   A. 摆位 —— 线近端离接缝正好 SeamGap；标签靠接缝那条边正好 SeamGap+TagGap；
///      三角尖越过接缝正好 TipOverSeam；所有元素都落在本屏之内、且不越过接缝。
///   B. 收敛 —— 给定物理错位 m 与任意两屏缩放比，按"用户拖到看起来成一条直线"的拖动量
///      走一遍 <see cref="AlignmentSolver.SolvePosition"/>，应用后两屏在标尺高度处的
///      物理位置必须一致（误差不超过半个逻辑像素）。
/// </summary>
public static class SelfCheck
{
    /// <summary>自检用的宿主矩形：不依赖真实硬件，四方位都在这块屏里切两半。</summary>
    private const int HostW = 2560, HostH = 1440;

    private static readonly (string Dir, string Name)[] Directions =
    [
        ("right", "副屏在右 · 左右并排"),
        ("left",  "副屏在左 · 左右并排"),
        ("above", "副屏在上 · 上下叠放"),
        ("below", "副屏在下 · 上下叠放"),
    ];

    private sealed class Stats { public int Pass; public int Fail; }

    /// <summary>跑完自检并把报告写到 exe 同目录 / 临时目录 / 标准输出，返回失败项数。</summary>
    public static int Run()
    {
        var sb = new StringBuilder();
        var st = new Stats();

        sb.AppendLine("=== MonitorAlign 几何自检 ===");
        sb.AppendLine($"宿主矩形：模拟 {HostW}x{HostH}（固定尺寸，与真实硬件无关）");
        sb.AppendLine("判据：线近端离接缝 = 6；三角尖越过接缝 = 2；");
        sb.AppendLine("　　　按钮条骑线中点：基准 [≡][↺] 沿线并排、拖动键靠接缝；目标 [微调][≡][微调] 沿移动轴成串；");
        sb.AppendLine("　　　标签/读数在条沿轴外侧（不与条重叠，也不再骑线）");
        sb.AppendLine();

        foreach (var (dir, name) in Directions)
        {
            sb.AppendLine($"--- {name} ---");
            var displays = DisplayEnumerator.ArrangeDemo(dir, 0, 0, HostW, HostH);
            var topo = TopologyAnalyzer.Analyze(displays);

            if (topo.Seams.Count != 1 || topo.Seams[0].Unreachable)
            {
                st.Fail++;
                sb.AppendLine($"  ✗ 接缝应当有且只有 1 条，实际 {topo.Seams.Count} 条");
                sb.AppendLine();
                continue;
            }

            var seam = topo.Seams[0];
            bool wantVertical = dir is "left" or "right";

            Check(st, sb, "接缝方向", seam.Vertical == wantVertical,
                  seam.Vertical ? "竖直" : "水平", wantVertical ? "竖直" : "水平");
            Check(st, sb, "基准侧是主屏", seam.Base.IsPrimary && seam.Base.Index == 1,
                  $"#{seam.Base.Index} → #{seam.Target.Index}", "#1 → 副屏");
            Check(st, sb, "副屏侧是另一台", !seam.Target.IsPrimary,
                  $"#{seam.Target.Index}", "非主屏");

            sb.AppendLine($"  接缝 {seam.DirectionText} @ {(seam.Vertical ? "x" : "y")}={seam.SeamCoord}" +
                          $"  有效区间 [{seam.OverlapStart}, {seam.OverlapEnd}]  参考物 {seam.MidPoint}");

            foreach (var (disp, isBase) in new[] { (seam.Base, true), (seam.Target, false) })
                CheckSide(st, sb, seam, disp, isBase);

            CheckCluster(st, sb, seam, seam.Target);
            CheckConvergence(st, sb, seam, seam.Vertical);
            CheckSequentialCommits(st, sb, seam, seam.Vertical);

            var placement = SeamGaugeGeometry.Placement(seam);
            var scaled = SeamGaugeGeometry.Solve(seam, 60, 140);
            double expected = placement.Length / (placement.Length + 80);
            Check(st, sb, "双标尺尺度比", Math.Abs(scaled.Ratio - expected) < 1e-9,
                  $"{scaled.Ratio}", $"{expected}");
            Check(st, sb, "双标尺目标分辨率",
                  scaled.Width == (int)Math.Round(seam.Target.PelsWidth * expected)
                  && scaled.Height == (int)Math.Round(seam.Target.PelsHeight * expected),
                  $"{scaled.Width}x{scaled.Height}", "当前分辨率乘尺度比");
            var translated = SeamGaugeGeometry.Solve(seam, 80, 80);
            Check(st, sb, "双标尺同移不改变比例", translated.Ratio == 1,
                  $"{translated.Ratio}", "1");
            sb.AppendLine();
        }

        foreach (int length in new[] { 1, 20, 100, 180, 1080 })
        {
            double inset = SeamGaugeGeometry.Inset(length);
            Check(st, sb, $"短接缝内缩 {length}px", double.IsFinite(inset) && inset >= 0,
                  $"{inset}", "有限非负且不抛异常");
        }

        CheckDisplayTransactions(st, sb);

        sb.AppendLine($"结论：共 {st.Pass + st.Fail} 项断言，通过 {st.Pass}，失败 {st.Fail}。");

        string report = sb.ToString();
        foreach (string where in new[]
                 {
                     Path.GetDirectoryName(Environment.ProcessPath),
                     AppContext.BaseDirectory,
                     Path.GetTempPath(),
                 })
        {
            if (string.IsNullOrEmpty(where)) continue;
            try { File.WriteAllText(Path.Combine(where, "MonitorAlign-geomcheck.txt"), report); }
            catch { }
        }
        try { Console.Out.Write(report); } catch { }

        return st.Fail;
    }

    /// <summary>一台屏上的摆位断言。</summary>
    private static void CheckSide(Stats st, StringBuilder sb, Seam seam, DisplayInfo disp, bool isBase)
    {
        bool negative = seam.Vertical
            ? disp.Right <= seam.SeamCoord + 1
            : disp.Bottom <= seam.SeamCoord + 1;

        double scale = disp.Scale <= 0 ? 1 : disp.Scale;
        double originAcross = seam.Vertical ? disp.Left : disp.Top;
        double sizeAcross = seam.Vertical ? disp.Width : disp.Height;

        string who = isBase ? "基准" : "副屏";
        sb.AppendLine($"  {who} #{disp.Index} {disp.Width}x{disp.Height} @({disp.Left},{disp.Top})" +
                      $"  位于接缝{(negative ? "负方向" : "正方向")}侧");

        var (a1, a2, near) = RulerGeometry.BarAcross(seam.SeamCoord, originAcross, scale, negative);

        // ---- 线 ----
        double nearLogical = near * scale + originAcross;
        Check(st, sb, "  线近端距接缝", Near(Math.Abs(nearLogical - seam.SeamCoord), RulerGeometry.SeamGapLogical),
              $"{Math.Abs(nearLogical - seam.SeamCoord):0.##}", $"{RulerGeometry.SeamGapLogical}");

        bool nearOnOwnSide = negative ? nearLogical < seam.SeamCoord : nearLogical > seam.SeamCoord;
        Check(st, sb, "  线近端未越过接缝", nearOnOwnSide,
              nearOnOwnSide ? "在本屏内" : "越到接缝对面");

        double farDil = Math.Abs(a1 - near) < 1e-9 ? a2 : a1;          // 不是近端的那一端
        double farLogical = farDil * scale + originAcross;
        bool farInside = farLogical >= originAcross && farLogical <= originAcross + sizeAcross
                         && (negative ? farLogical < nearLogical : farLogical > nearLogical);
        Check(st, sb, "  线远端在本屏内", farInside,
              farInside ? $"尚余 {Math.Abs(farLogical - originAcross):0} px" : $"落在 {farLogical:0}，屏为 [{originAcross:0},{originAcross + sizeAcross:0}]");

        // ---- 三角标 ----
        double tipLogical = RulerGeometry.TriangleTipAcross(seam.SeamCoord, originAcross, scale, negative) * scale + originAcross;
        double over = tipLogical - seam.SeamCoord;
        bool tipOk = Near(Math.Abs(over), RulerGeometry.TipOverSeam)
                     && (negative ? over > 0 : over < 0);               // 必须朝接缝对面越过去
        Check(st, sb, "  三角尖越过接缝", tipOk,
              $"{over:+0.##;-0.##}", $"{(negative ? "+" : "-")}{RulerGeometry.TipOverSeam}");

        if (!isBase) return;                                            // 以下只有基准侧有

        // ---- 基准按钮条 [≡][↺]：骑在线中点，拖动键靠接缝一侧 ----
        double midDip = (a1 + a2) / 2;
        double extent = RulerGeometry.ButtonBarExtent(RulerGeometry.BaseBarButtonCount);
        double halfExtentDip = extent / 2 / scale;

        bool barInside = midDip - halfExtentDip >= a1 && midDip + halfExtentDip <= a2;
        Check(st, sb, "  按钮条在线跨度内", barInside,
              barInside ? $"条跨 {extent:0} / 线长 {RulerGeometry.BarLength}" : "条越出了线两端");

        // 拖动键中心：条端内缩 (Pad + 半键) 处，靠接缝一侧
        double dragOffset = extent / 2 - RulerGeometry.ButtonBarPad - RulerGeometry.BarButtonSize / 2;
        double dragCenter = midDip - RulerGeometry.AwaySign(negative) * dragOffset / scale;
        bool dragTowardSeam = negative ? dragCenter > midDip : dragCenter < midDip;
        Check(st, sb, "  拖动键在靠接缝一侧", dragTowardSeam,
              $"偏移 {Math.Abs(dragCenter - midDip) * scale:0} px（重置键在远接缝侧）");

        // ---- 基准标签：条沿轴外侧，不再骑线、不再被按钮遮挡 ----
        // 标签沿轴起点（相对线所在沿轴位置）：条半厚 + 间距
        double tagAlongStart = RulerGeometry.ButtonBarThickness / 2 + RulerGeometry.TagGapAlong;
        double barAlongEnd = RulerGeometry.ButtonBarThickness / 2;
        Check(st, sb, "  标签在条沿轴外侧", tagAlongStart >= barAlongEnd,
              $"沿轴间距 {tagAlongStart - barAlongEnd:0} px");

        // 标签横跨居中于条中心（across），不越出本屏
        double tagExtent = seam.Vertical ? 140 : 24;          // 中英文里较宽的估算
        double tagLeft = midDip - tagExtent / 2 / scale;
        double tagRight = midDip + tagExtent / 2 / scale;
        bool tagInside = tagLeft >= 0 && tagRight * scale <= originAcross + sizeAcross;
        Check(st, sb, "  标签整体在本屏内", tagInside,
              $"中心 {midDip * scale + originAcross - originAcross:0} / 屏宽 {sizeAcross:0}");
        return;
    }

    /// <summary>目标侧按钮条 [微调][≡][微调] 与读数浮标的摆位断言 —— 四方位都必须镜像正确。</summary>
    private static void CheckCluster(Stats st, StringBuilder sb, Seam seam, DisplayInfo target)
    {
        bool negative = seam.Vertical
            ? target.Right <= seam.SeamCoord + 1
            : target.Bottom <= seam.SeamCoord + 1;

        double scale = target.Scale <= 0 ? 1 : target.Scale;
        double originAcross = seam.Vertical ? target.Left : target.Top;
        double sizeAcross = seam.Vertical ? target.Width : target.Height;

        double seamDip = (seam.SeamCoord - originAcross) / scale;
        double near = seamDip + (negative ? -RulerGeometry.SeamGap : RulerGeometry.SeamGap) / scale;
        double far = near + RulerGeometry.AwaySign(negative) * RulerGeometry.BarLength / scale;
        double lineMid = (near + far) / 2;

        int n = RulerGeometry.TargetBarButtonCount;
        double extent = RulerGeometry.ButtonBarExtent(n);
        double thickness = RulerGeometry.ButtonBarThickness;

        // ---- 条中心骑在线中点（across），不再偏到"远接缝一侧" ----
        // 线中点距接缝 = BarLength/2 + SeamGap（near 贴缝、far 伸进屏内 180）
        double barCenter = (near + far) / 2;
        Check(st, sb, "  按钮条骑线中点",
              Near(Math.Abs(barCenter - seamDip), (RulerGeometry.BarLength / 2 + RulerGeometry.SeamGap) / scale),
              $"距接缝 {Math.Abs(barCenter - seamDip) * scale:0} px");

        // ---- 条的 across 覆盖（thickness）收在线跨度内 ----
        bool barInside = lineMid - thickness / 2 / scale >= Math.Min(near, far)
                      && lineMid + thickness / 2 / scale <= Math.Max(near, far);
        Check(st, sb, "  按钮条在线跨度内", barInside,
              barInside ? $"条厚 {thickness:0} / 线长 {RulerGeometry.BarLength}" : "条越出了线两端");

        // ---- 拖动键居中、两枚微调键关于它对称（沿移动轴）----
        // extent(3)=88：半长 44 − 内边距 3 − 半键 13 = 28
        double nudgeOffset = extent / 2 - RulerGeometry.ButtonBarPad - RulerGeometry.BarButtonSize / 2;
        Check(st, sb, "  微调键关于拖动键对称", Near(nudgeOffset, 28),
              $"偏移 ±{nudgeOffset:0} px（拖动键居中）");

        // ---- 读数浮标：条沿轴外侧（偏移 = 条沿移动轴总长一半 + 间距，不是条厚一半）----
        double readoutAlongStart = seam.MidPoint
            + RulerGeometry.ButtonBarExtent(RulerGeometry.TargetBarButtonCount) / 2
            + RulerGeometry.ReadoutGapAlong;
        bool readoutAfterBar = readoutAlongStart >= seam.MidPoint
            + RulerGeometry.ButtonBarExtent(RulerGeometry.TargetBarButtonCount) / 2;
        Check(st, sb, "  读数在条沿轴外侧", readoutAfterBar,
              $"沿轴起点 {readoutAlongStart:0} / 锚点 {seam.MidPoint}");

        // 读数沿轴方向的占用（估 96 逻辑像素宽）不能超出接缝有效区间太多
        double readoutAlongEnd = readoutAlongStart + 96;
        bool readoutInRange = readoutAlongEnd <= seam.OverlapEnd + 120;
        Check(st, sb, "  读数沿轴不飞出接缝区间", readoutInRange,
              $"沿轴末端 {readoutAlongEnd:0} / 区间末端 {seam.OverlapEnd}");
    }

    /// <summary>补偿换算的收敛断言：应用之后，两屏在标尺高度处的物理位置必须一致。</summary>
    private static void CheckConvergence(Stats st, StringBuilder sb, Seam seam, bool vertical)
    {
        int posBase = vertical ? seam.Base.PositionY : seam.Base.PositionX;
        int posTarget = vertical ? seam.Target.PositionY : seam.Target.PositionX;
        double mid = seam.MidPoint;

        const double physBase = 1000;      // 基准屏"物理上/左边缘"的假定值（毫米），只用于建模

        foreach (double m in new[] { -37.0, -5, 0, 5, 37 })
        {
            foreach (var (sBase, sTarget) in new[] { (1.0, 1.0), (1.0, 1.25), (1.5, 1.0) })
            {
                // k = 物理边缘 − 逻辑边缘 × 缩放；同一条逻辑线落在两屏上的物理位置一致 ⟺ 对齐
                double kBase = physBase - posBase * sBase;
                double kTarget = physBase + m - posTarget * sTarget;

                // 用户把标尺拖到"看起来成一条直线"处
                double marker = (kBase + mid * sBase - kTarget) / sTarget;
                int delta = (int)Math.Round(marker - mid);

                var (nx, ny) = AlignmentSolver.SolvePosition(vertical,
                    seam.Target.PositionX, seam.Target.PositionY, delta);
                double kTargetNew = physBase + m - (vertical ? ny : nx) * sTarget;

                double residual = (kTargetNew + mid * sTarget) - (kBase + mid * sBase);
                bool ok = Math.Abs(residual) <= 0.5 * sTarget + 1e-9;

                Check(st, sb, $"  收敛 m={m:+0;-#} 缩放({sBase:0.##},{sTarget:0.##})", ok,
                      $"残差 {residual:+0.###;-0.###} mm",
                      Math.Abs(residual) <= 0.5 * sTarget + 1e-9 ? "" : "≤ 半个逻辑像素");
            }
        }
    }

    /// <summary>
    /// 连续多次提交必须只算增量（OverlayWindow.PendingDelta 的记账模型）。
    ///
    /// 曾经的算法是 marker − 实时接缝中点：提交 → 排列变 → 中点漂移 →
    /// 下一次提交把已应用过的量再算一遍。实测连点 5 次 ▼ 的提交序列是 1,3,7,15
    /// （第 n 次 delta = n + 已累计量），总共写了 52px 而不是 5px。
    /// 这里按同样的模型断言：对齐→点一下→再对齐，最终必须仍然对齐。
    /// </summary>
    private static void CheckSequentialCommits(Stats st, StringBuilder sb, Seam seam, bool vertical)
    {
        int posBase = vertical ? seam.Base.PositionY : seam.Base.PositionX;
        int posTarget0 = vertical ? seam.Target.PositionY : seam.Target.PositionX;
        double mid = seam.MidPoint;
        const double physBase = 1000;
        const double m = 23.0;             // 假定物理错位
        const double sB = 1.0, sT = 1.0;

        double kBase = physBase - posBase * sB;
        double kTarget = physBase + m - posTarget0 * sT;

        // 标尺被钉在屏幕上（覆盖层窗口随屏移动），所以"对齐位置"恒为：
        //   physTopT + (marker − posT快照)·sT == physTopB + (mid − posB)·sB
        double alignedMarker = mid + ((kBase + mid * sB) - (kTarget + mid * sT)) / sT;

        double pos = posTarget0;           // 当前 dmPosition
        double lastCommitted = mid;        // OverlayWindow._lastCommitted

        double Residual(double p)
        {
            double kT = physBase + m - p * sT;
            return (kT + mid * sT) - (kBase + mid * sB);
        }

        // ① 用户拖到对齐并提交
        double pending = alignedMarker - lastCommitted;
        pos -= (int)Math.Round(pending);
        lastCommitted = alignedMarker;
        double r1 = Residual(pos);
        Check(st, sb, "  连续提交 · 对齐后提交", Math.Abs(r1) < 1e-9, $"残差 {r1:+0.###;-0.###}");

        // ② 又点了一下 ▼（+1）
        pos -= (int)Math.Round((alignedMarker + 1) - lastCommitted);
        lastCommitted = alignedMarker + 1;
        double r2 = Residual(pos);
        Check(st, sb, "  连续提交 · 再点一下(+1)", Math.Abs(r2 - 1) < 1e-9, $"残差 {r2:+0.###;-0.###}（应恰为 1）");

        // ③ 用户把标尺拖回对齐位置再提交 → 总量回到恰好对齐
        pos -= (int)Math.Round(alignedMarker - lastCommitted);
        lastCommitted = alignedMarker;
        double r3 = Residual(pos);
        Check(st, sb, "  连续提交 · 拖回再提交", Math.Abs(r3) < 1e-9, $"残差 {r3:+0.###;-0.###}");

        // ④ 对照：旧算法（marker − 漂移后的实时中点）在同样序列下会写多少。
        //    第 n 次点击 delta = n + 已累计量 → 1,3,7,15,31，共 57px。
        //    （真机实测日志截到第 4 次提交 = 26px，排列位移与它吻合。）
        double posOld = posTarget0;
        double marker = mid;
        double liveMid = mid;              // 实时中点：每次提交后跟着排列漂移
        for (int i = 1; i <= 5; i++)
        {
            marker += 1;                   // 第 i 次点击
            posOld -= (int)Math.Round(marker - liveMid);
            liveMid -= (int)Math.Round(marker - liveMid);   // 排列动了 → 中点漂移
        }
        Check(st, sb, "  连续提交 · 旧算法复现(5次点击)", Near(posOld - posTarget0, -57),
              $"{posOld - posTarget0:+0;-#} px（1+3+7+15+31=57，即反馈的叠加 bug）", "-57");
    }

    private static List<DisplayInfo> TransactionDisplays() => Enumerable.Range(0, 3)
        .Select(i => new DisplayInfo
        {
            Index = i + 1,
            DeviceName = $"FAKE-{i + 1}",
            IsPrimary = i == 0,
            PositionX = i * 100,
            Rect = new System.Windows.Rect(i * 100, 0, 100, 80),
            PelsWidth = 200,
            PelsHeight = 160,
            Frequency = 60,
        }).ToList();

    private sealed class FakeDisplaySettings : DisplayArrangement.IDisplaySettings
    {
        private readonly List<DisplayInfo> _models;
        private readonly List<MonitorPosition> _initial;
        private readonly Dictionary<string, Win32.DEVMODE> _current = new();
        private readonly Dictionary<string, Win32.DEVMODE> _pending = new();
        public readonly List<(string Device, Win32.DEVMODE Mode)> Staged = new();
        public readonly HashSet<int> FailStages = new();
        public readonly HashSet<int> FailCommits = new();
        public string FailQuery;
        public bool ThrowOnApi;
        public int Queries;
        public int Commits;
        public int ApiCalls;
        public int QueriesBeforeFirstWrite = -1;
        public bool ModelsUnchangedDuringWrites = true;

        public FakeDisplaySettings(List<DisplayInfo> models)
        {
            _models = models;
            _initial = DisplayArrangement.Snapshot(models);
            foreach (var display in models)
                _current[display.DeviceName] = new Win32.DEVMODE
                {
                    dmFields = Win32.DM_POSITION | 0x00080000u | 0x00100000u | 0x00400000u,
                    dmPositionX = display.PositionX,
                    dmPositionY = display.PositionY,
                    dmPelsWidth = (uint)display.PelsWidth,
                    dmPelsHeight = (uint)display.PelsHeight,
                    dmDisplayFrequency = (uint)display.Frequency,
                };
        }

        private void Api()
        {
            ApiCalls++;
            if (ThrowOnApi) throw new InvalidOperationException("演示事务不得调用显示 API");
        }

        private void BeforeWrite()
        {
            if (QueriesBeforeFirstWrite < 0) QueriesBeforeFirstWrite = Queries;
            ModelsUnchangedDuringWrites &= DisplayArrangement.Snapshot(_models).SequenceEqual(_initial);
        }

        public bool TryGetCurrentMode(string deviceName, out Win32.DEVMODE mode)
        {
            Api();
            Queries++;
            mode = _current[deviceName];
            return deviceName != FailQuery;
        }

        public int StageMode(string deviceName, Win32.DEVMODE mode)
        {
            Api();
            BeforeWrite();
            Staged.Add((deviceName, mode));
            // 故意让失败调用也留下暂存副作用，验证失败设备同样进入恢复范围。
            if (mode.dmFields == Win32.DM_POSITION)
            {
                var original = _current[deviceName];
                original.dmPositionX = mode.dmPositionX;
                original.dmPositionY = mode.dmPositionY;
                _pending[deviceName] = original;
            }
            else
                _pending[deviceName] = mode;
            return FailStages.Contains(Staged.Count) ? Win32.DISP_CHANGE_FAILED : Win32.DISP_CHANGE_SUCCESSFUL;
        }

        public int Commit()
        {
            Api();
            BeforeWrite();
            Commits++;
            foreach (var item in _pending) _current[item.Key] = item.Value;
            _pending.Clear();
            if (!FailCommits.Contains(Commits)) return Win32.DISP_CHANGE_SUCCESSFUL;
            // 模拟全局提交部分生效，连未暂存设备也发生偏移。
            foreach (var key in _current.Keys.ToList())
            {
                var mode = _current[key];
                mode.dmPositionX += 7;
                _current[key] = mode;
            }
            return Win32.DISP_CHANGE_FAILED;
        }

        public IReadOnlyList<DisplayInfo> Enumerate()
        {
            Api();
            return _models.Select(display =>
            {
                var mode = _current[display.DeviceName];
                return new DisplayInfo
                {
                    DeviceName = display.DeviceName,
                    PositionX = mode.dmPositionX,
                    PositionY = mode.dmPositionY,
                    Rect = new System.Windows.Rect(mode.dmPositionX, mode.dmPositionY, 100, 80),
                    PelsWidth = (int)mode.dmPelsWidth,
                    PelsHeight = (int)mode.dmPelsHeight,
                    Frequency = (int)mode.dmDisplayFrequency,
                    IsPrimary = display.IsPrimary,
                };
            }).ToList();
        }
    }

    private static void CheckDisplayTransactions(Stats st, StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("--- 显示排列事务（纯内存假 API，不操作真实显示器） ---");
        var displays = TransactionDisplays();
        var initial = DisplayArrangement.Snapshot(displays);
        var fake = new FakeDisplaySettings(displays) { FailQuery = "FAKE-2" };
        var desired = initial.Select(p => p with { Y = 25 }).ToList();
        bool ok = DisplayArrangement.Apply(displays, desired, false, fake, out var error);
        Check(st, sb, "事务 · 查询失败零写入", !ok && fake.Staged.Count == 0 && fake.Commits == 0,
              $"成功={ok} 暂存={fake.Staged.Count} 提交={fake.Commits}");
        Check(st, sb, "事务 · 查询失败模型不污染", DisplayArrangement.Snapshot(displays).SequenceEqual(initial),
              string.Join(",", displays.Select(d => d.PositionY)), "全部为 0");

        displays = TransactionDisplays();
        initial = DisplayArrangement.Snapshot(displays);
        fake = new FakeDisplaySettings(displays);
        fake.FailStages.Add(2);
        ok = DisplayArrangement.Apply(displays, desired, false, fake, out error);
        Check(st, sb, "事务 · 暂存失败停止后续设备并恢复已尝试设备",
              !ok && fake.Staged.Select(s => s.Device).SequenceEqual(new[] { "FAKE-1", "FAKE-2", "FAKE-1", "FAKE-2" })
              && fake.Commits == 1 && DisplayArrangement.Snapshot(displays).SequenceEqual(initial),
              $"成功={ok} 写入顺序={string.Join(",", fake.Staged.Select(s => s.Device))} 提交={fake.Commits}");
        Check(st, sb, "事务 · 所有查询先于首写且写入期间模型不变",
              fake.QueriesBeforeFirstWrite == displays.Count && fake.ModelsUnchangedDuringWrites,
              $"首写前查询={fake.QueriesBeforeFirstWrite} 模型未变={fake.ModelsUnchangedDuringWrites}");
        Check(st, sb, "事务 · 恢复保留原模式尺寸和字段",
              fake.Staged.Skip(2).All(s => s.Mode.dmFields != Win32.DM_POSITION
                  && s.Mode.dmPelsWidth == 200 && s.Mode.dmPelsHeight == 160 && s.Mode.dmDisplayFrequency == 60),
              "检查恢复暂存的完整 DEVMODE");

        displays = TransactionDisplays();
        initial = DisplayArrangement.Snapshot(displays);
        fake = new FakeDisplaySettings(displays);
        fake.FailCommits.Add(1);
        var oneChanged = initial.Select(p => p.DeviceName == "FAKE-2" ? p with { Y = 25 } : p).ToList();
        ok = DisplayArrangement.Apply(displays, oneChanged, false, fake, out error);
        Check(st, sb, "事务 · 提交失败恢复全部原模式（含不变设备）",
              !ok && fake.Commits == 2 && fake.Staged.Count == 4
              && fake.Staged.Skip(1).Select(s => s.Device).SequenceEqual(new[] { "FAKE-1", "FAKE-2", "FAKE-3" })
              && DisplayArrangement.Snapshot(displays).SequenceEqual(initial),
              $"成功={ok} 暂存={fake.Staged.Count} 提交={fake.Commits}");

        displays = TransactionDisplays();
        fake = new FakeDisplaySettings(displays);
        fake.FailCommits.UnionWith(new[] { 1, 2 });
        ok = DisplayArrangement.Apply(displays, oneChanged, false, fake, out error);
        Check(st, sb, "事务 · 恢复失败明确报告并同步实际坐标",
              !ok && error != null && error.Contains("commit: -1") && displays[0].PositionX == 7,
              $"成功={ok} 实际首屏X={displays[0].PositionX} 错误={error}");

        displays = TransactionDisplays();
        fake = new FakeDisplaySettings(displays);
        int displayEnumerations = 0, positionEnumerations = 0;
        IEnumerable<DisplayInfo> OnceDisplays()
        {
            if (++displayEnumerations != 1) throw new InvalidOperationException("显示器枚举重复消费");
            foreach (var display in displays) yield return display;
        }
        IEnumerable<MonitorPosition> OncePositions()
        {
            if (++positionEnumerations != 1) throw new InvalidOperationException("位置枚举重复消费");
            foreach (var position in oneChanged) yield return position;
        }
        ok = DisplayArrangement.Apply(OnceDisplays(), OncePositions(), false, fake, out error);
        Check(st, sb, "事务 · 成功提交后才更新模型且跳过不变设备",
              ok && error == null && fake.ModelsUnchangedDuringWrites && fake.Staged.Count == 1 && fake.Commits == 1
              && DisplayArrangement.Snapshot(displays).SequenceEqual(oneChanged),
              $"成功={ok} 写入期间未改模型={fake.ModelsUnchangedDuringWrites} 暂存={fake.Staged.Count}");
        Check(st, sb, "事务 · 两个输入序列仅消费一次", displayEnumerations == 1 && positionEnumerations == 1,
              $"显示器={displayEnumerations} 位置={positionEnumerations}");
        Check(st, sb, "事务 · Rect 桌面尺寸不被 Pels 模式尺寸覆盖",
              displays.All(d => d.Width == 100 && d.Height == 80 && d.PelsWidth == 200 && d.PelsHeight == 160),
              "Rect=100x80 Pels=200x160");

        fake = new FakeDisplaySettings(displays);
        ok = DisplayArrangement.Apply(displays, DisplayArrangement.Snapshot(displays), false, fake, out error);
        Check(st, sb, "事务 · 全部不变不暂存不提交", ok && fake.Staged.Count == 0 && fake.Commits == 0,
              $"成功={ok} 暂存={fake.Staged.Count} 提交={fake.Commits}");

        displays = TransactionDisplays();
        fake = new FakeDisplaySettings(displays) { ThrowOnApi = true };
        ok = DisplayArrangement.Apply(displays, desired, true, fake, out error);
        Check(st, sb, "事务 · 演示模式零 API 且仅移动矩形",
              ok && fake.ApiCalls == 0 && DisplayArrangement.Snapshot(displays).SequenceEqual(desired)
              && displays.All(d => d.Width == 100 && d.Height == 80 && d.PelsWidth == 200 && d.PelsHeight == 160),
              $"成功={ok} API={fake.ApiCalls} 矩形={displays[0].Width}x{displays[0].Height}");
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static void Check(Stats st, StringBuilder sb, string label, bool ok, string actual, string expected = "")
    {
        if (ok)
        {
            st.Pass++;
            sb.AppendLine($"  ✓ {label} = {actual}");
        }
        else
        {
            st.Fail++;
            sb.AppendLine($"  ✗ {label} = {actual}" + (expected.Length > 0 ? $"（期望 {expected}）" : ""));
        }
    }
}
