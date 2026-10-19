namespace MonitorAlign.Core;

/// <summary>
/// 一条接缝：Base 为基准屏（靠近主屏一侧），Target 为待调整屏。
/// Vertical=true 表示接缝是竖直的（两屏左右并排，需要修正 Y）；
/// Vertical=false 表示接缝是水平的（两屏上下叠放，需要修正 X）。
/// </summary>
public sealed class Seam
{
    public DisplayInfo Base;
    public DisplayInfo Target;

    public bool Vertical;
    public int SeamCoord;                 // 竖直接缝的 x / 水平接缝的 y
    public int OverlapStart, OverlapEnd;  // 接缝有效区间（沿接缝方向）
    public int Gap;                       // 法线方向间隙：正=有缝，负=重叠
    public int Depth = 1;                 // BFS 深度（相对主屏的对齐链层数）
    public bool Unreachable;              // 与主屏不连通（孤屏），无法用对齐链处理

    public int OverlapLength => OverlapEnd - OverlapStart;
    public string Label => $"#{Base.Index} → #{Target.Index}";
    public string DirectionText => Vertical ? Loc.SeamVertical : Loc.SeamHorizontal;

    /// <summary>参考物默认落点：接缝有效区间中点。</summary>
    public int MidPoint => (OverlapStart + OverlapEnd) / 2;
}

public sealed class Topology
{
    public List<DisplayInfo> Displays = new();
    public List<Seam> Seams = new();
    public DisplayInfo Primary;

    public int VerticalSeamCount => Seams.Count(s => s.Vertical);
    public int HorizontalSeamCount => Seams.Count(s => !s.Vertical);
    public int MaxDepth => Seams.Count == 0 ? 0 : Seams.Max(s => s.Depth);

    /// <summary>虚拟桌面整体包围盒，用于画拓扑缩略图。</summary>
    public (double Left, double Top, double Width, double Height) Bounds()
    {
        double l = Displays.Min(d => d.Rect.Left);
        double t = Displays.Min(d => d.Rect.Top);
        double r = Displays.Max(d => d.Rect.Right);
        double b = Displays.Max(d => d.Rect.Bottom);
        return (l, t, r - l, b - t);
    }
}

public static class TopologyAnalyzer
{
    /// <summary>判定两屏相邻时允许的间隙容差（逻辑像素）。</summary>
    public const int AdjacencyTolerance = 8;

    /// <summary>接缝有效区间的最小长度，短于此认为不可对齐。</summary>
    public const int MinOverlap = 32;

    public static Topology Analyze(List<DisplayInfo> displays)
    {
        var topo = new Topology { Displays = displays };
        if (displays.Count == 0) return topo;

        topo.Primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];

        // 邻接表：无向，双向登记（后面 BFS 决定谁是 Base 谁是 Target）
        var adj = new Dictionary<int, List<Seam>>();
        foreach (var d in displays) adj[d.Index] = new List<Seam>();

        for (int i = 0; i < displays.Count; i++)
        {
            for (int j = i + 1; j < displays.Count; j++)
            {
                var seam = TryMakeSeam(displays[i], displays[j]);
                if (seam == null) continue;
                adj[displays[i].Index].Add(seam);
                // 反向副本
                adj[displays[j].Index].Add(new Seam
                {
                    Base = seam.Target, Target = seam.Base,
                    Vertical = seam.Vertical, SeamCoord = seam.SeamCoord,
                    OverlapStart = seam.OverlapStart, OverlapEnd = seam.OverlapEnd,
                    Gap = seam.Gap
                });
            }
        }

        // BFS：以主屏为根，父节点作为 Base
        var visited = new HashSet<int> { topo.Primary.Index };
        var depthOf = new Dictionary<int, int> { [topo.Primary.Index] = 0 };
        var treePairs = new HashSet<(int, int)>();   // 已作为树边的屏对（交叉边要去重用）
        var queue = new Queue<(DisplayInfo node, int depth)>();
        queue.Enqueue((topo.Primary, 0));

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            foreach (var seam in adj[node.Index])
            {
                if (visited.Contains(seam.Target.Index)) continue;
                visited.Add(seam.Target.Index);
                seam.Depth = depth + 1;
                depthOf[seam.Target.Index] = depth + 1;
                treePairs.Add((Math.Min(seam.Base.Index, seam.Target.Index),
                               Math.Max(seam.Base.Index, seam.Target.Index)));
                topo.Seams.Add(seam);
                queue.Enqueue((seam.Target, depth + 1));
            }
        }

        // 交叉边：两台都已连通的屏若相邻（如 demo 里 5 与 8），它们之间同样是一条
        // 可对齐的接缝 —— BFS 只认树边，会把这种接缝丢掉，缩略图上就少一个接缝点。
        // 方向约定不变：靠主屏近的一侧当基准，调整的是更深的那台。
        for (int i = 0; i < displays.Count; i++)
        {
            for (int j = i + 1; j < displays.Count; j++)
            {
                var a = displays[i];
                var b = displays[j];
                if (!visited.Contains(a.Index) || !visited.Contains(b.Index)) continue;
                if (treePairs.Contains((a.Index, b.Index))) continue;

                var (da, db) = (depthOf[a.Index], depthOf[b.Index]);
                Seam cross = da <= db ? TryMakeSeam(a, b) : TryMakeSeam(b, a);
                if (cross == null) continue;
                cross.Depth = Math.Max(da, db);
                topo.Seams.Add(cross);
            }
        }

        // 未连通的孤屏（不与主屏相邻）也要列出来，UI 上给出提示
        foreach (var d in displays)
            if (!visited.Contains(d.Index))
                topo.Seams.Add(new Seam { Base = topo.Primary, Target = d, Vertical = true, Unreachable = true });

        return topo;
    }

    /// <summary>
    /// 判断 a、b 是否构成一条接缝；是则返回以 a 为 Base 的 Seam。
    /// 若同时满足多个方位（角接触），取重叠区间最长的那个。
    /// </summary>
    private static Seam TryMakeSeam(DisplayInfo a, DisplayInfo b)
    {
        var candidates = new List<Seam>();

        // b 在 a 右侧 → 垂直接缝
        if (Math.Abs(a.Right - b.Left) <= AdjacencyTolerance)
        {
            int s = Math.Max(a.Top, b.Top), e = Math.Min(a.Bottom, b.Bottom);
            if (e - s >= MinOverlap)
                candidates.Add(new Seam { Base = a, Target = b, Vertical = true, SeamCoord = a.Right,
                    OverlapStart = s, OverlapEnd = e, Gap = b.Left - a.Right });
        }
        // b 在 a 左侧
        if (Math.Abs(a.Left - b.Right) <= AdjacencyTolerance)
        {
            int s = Math.Max(a.Top, b.Top), e = Math.Min(a.Bottom, b.Bottom);
            if (e - s >= MinOverlap)
                candidates.Add(new Seam { Base = a, Target = b, Vertical = true, SeamCoord = a.Left,
                    OverlapStart = s, OverlapEnd = e, Gap = a.Left - b.Right });
        }
        // b 在 a 下方 → 水平接缝
        if (Math.Abs(a.Bottom - b.Top) <= AdjacencyTolerance)
        {
            int s = Math.Max(a.Left, b.Left), e = Math.Min(a.Right, b.Right);
            if (e - s >= MinOverlap)
                candidates.Add(new Seam { Base = a, Target = b, Vertical = false, SeamCoord = a.Bottom,
                    OverlapStart = s, OverlapEnd = e, Gap = b.Top - a.Bottom });
        }
        // b 在 a 上方
        if (Math.Abs(a.Top - b.Bottom) <= AdjacencyTolerance)
        {
            int s = Math.Max(a.Left, b.Left), e = Math.Min(a.Right, b.Right);
            if (e - s >= MinOverlap)
                candidates.Add(new Seam { Base = a, Target = b, Vertical = false, SeamCoord = a.Top,
                    OverlapStart = s, OverlapEnd = e, Gap = a.Top - b.Bottom });
        }

        if (candidates.Count == 0) return null;
        return candidates.OrderByDescending(c => c.OverlapLength).First();
    }
}
