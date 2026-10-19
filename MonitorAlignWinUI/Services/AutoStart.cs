using Microsoft.Win32;

namespace MonitorAlign.Services;

/// <summary>
/// 开机自启：写 HKCU 的 Run 键（不需要管理员权限）。
/// 启动参数带 <c>--background</c> —— 自启时不弹主窗口，只留托盘与泛光。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MonitorAlign";

    public static bool Get()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue(RunValueName) is string;
        }
        catch (System.Exception ex)
        {
            Diag.Log("读取开机自启失败：" + ex.Message);
            return false;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (on)
                k.SetValue(RunValueName, $"\"{System.Environment.ProcessPath}\" --background");
            else
                k.DeleteValue(RunValueName, throwOnMissingValue: false);
            Diag.Log($"开机自启已{(on ? "开启" : "关闭")}");
        }
        catch (System.Exception ex)
        {
            Diag.Log("设置开机自启失败：" + ex.Message);
        }
    }
}
