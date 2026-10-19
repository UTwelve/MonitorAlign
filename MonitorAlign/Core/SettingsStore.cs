using System.IO;
using System.Text.Json;

namespace MonitorAlign.Core;

/// <summary>
/// 设置存档：记录用户的对齐位置（每条接缝的标尺锚点、最后选中的接缝）。
///
/// 只存"界面状态"，不存对齐偏移量 —— 偏移量本身已经由
/// ChangeDisplaySettingsEx(CDS_UPDATEREGISTRY) 写进系统，重启仍在；
/// 存档再存一份反而会在用户用别的方式改过排列后把状态搞乱。
///
/// 排列签名（设备 ID + 逻辑矩形 + 缩放）与存档不符时整个存档作废重置：
/// 锚点数值只有在"当时的排列"下才有意义。
///
/// 演示模式（--demo / --arrange）不读不写不重置，保护真机存档。
/// </summary>
public sealed class AlignSettings
{
    public string Signature { get; set; } = "";
    public int SelectedTarget { get; set; } = -1;
    public Dictionary<string, double> Anchors { get; set; } = new();

    /// <summary>边缘泛光开关。严格说与排列无关，但跟着存档一起重置代价可接受（一个开关而已）。</summary>
    public bool EdgeGlowEnabled { get; set; }

    // ---- 边缘泛光可调参数（v1.6.0）----
    // 接缝颜色表依赖排列（seamKey 是屏编号对），跟锚点一样随签名重置；
    // 灵敏度/亮度/距离本身与排列无关，但放在同一份档案里，重置了也无伤大雅。

    /// <summary>接近灵敏度（倍数）：光标多近起辉 = 120px ÷ 倍数。</summary>
    public double EdgeGlowSensitivity { get; set; } = 1.0;

    /// <summary>发光亮度 0.05..1。</summary>
    public double EdgeGlowBrightness { get; set; } = 0.6;

    /// <summary>光晕单侧照射距离（逻辑像素）。不设上限，只挡异常值（太大生成不了窗口）。</summary>
    public double EdgeGlowDistance { get; set; } = 96;

    /// <summary>
    /// 尾部白度（「加性强度」）0..1 —— v2.0.0 新增。
    /// 老存档里没有这个字段，反序列化时构造函数先跑、字段保持这里的默认值，
    /// 所以从 v1.6.0 升上来的用户直接拿到 0.25，不需要写迁移代码。
    /// </summary>
    public double EdgeGlowTailWhiteness { get; set; } = 0.25;

    /// <summary>接缝自定义颜色：seamKey("#1->#2") → "#RRGGBB"。</summary>
    public Dictionary<string, string> EdgeGlowColors { get; set; } = new();
}

public static class SettingsStore
{
    public const string FileName = "MonitorAlign.settings.json";

    private static string Dir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string FilePath => Path.Combine(Dir, FileName);

    /// <summary>排列签名：覆盖"哪几台屏、各自在哪、缩放多少"的全部信息。</summary>
    public static string ComputeSignature(IReadOnlyList<DisplayInfo> displays) =>
        string.Join("|", displays
            .OrderBy(d => d.DeviceName, StringComparer.Ordinal)
            .Select(d => $"{d.DeviceId}@{d.Left},{d.Top},{d.Right},{d.Bottom}x{d.Scale:0.###}"));

    /// <summary>读档；文件不存在或损坏都返回 null（当作没有存档）。</summary>
    public static AlignSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var s = JsonSerializer.Deserialize<AlignSettings>(File.ReadAllText(FilePath));
            if (s == null) return null;
            s.Anchors ??= new();
            s.EdgeGlowColors ??= new();
            s.Signature ??= "";
            return s;
        }
        catch (Exception ex)
        {
            Diag.Log("设置存档读取失败：" + ex.Message);
            return null;
        }
    }

    public static void Save(AlignSettings s)
    {
        try
        {
            string tempPath = FilePath + ".tmp";
            string json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes(json);
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
            else File.Move(tempPath, FilePath);
        }
        catch (Exception ex)
        {
            Diag.Log("设置存档写入失败：" + ex.Message);
        }
    }

    /// <summary>排列不符时重置存档（删文件）。</summary>
    public static void Reset()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) { Diag.Log("设置存档重置失败：" + ex.Message); }
    }
}
