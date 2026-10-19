using System.IO;

namespace MonitorAlign;

/// <summary>
/// 诊断日志：仅 --diag / --navtest 等自检模式下写入临时目录。
/// 与 WPF 版同名同形（SettingsStore 等共享源码引用它），实现各自持有。
/// </summary>
public static class Diag
{
    public static bool Enabled;
    public static string LogPath => Path.Combine(Path.GetTempPath(), "MonitorAlign-diag.txt");

    public static void Log(string message)
    {
        if (!Enabled) return;
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}\r\n"); }
        catch { }
    }

    public static void Reset()
    {
        if (!Enabled) return;
        try { File.WriteAllText(LogPath, ""); } catch { }
    }
}
