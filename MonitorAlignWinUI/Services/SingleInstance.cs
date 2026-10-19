using System;
using System.Diagnostics;
using System.Threading;
using MonitorAlign.Native;

namespace MonitorAlign.Services;

/// <summary>
/// 单实例：第二次启动不新开一份，而是把已经在跑的那个叫出来。
///
/// 名字带 <c>WinUI3</c> 后缀 —— 两个版本（WPF / WinUI 3）并存时互相当对方是陌生人，
/// 否则同时调试两版会互相顶掉。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\MonitorAlign.UTwelve.WinUI3.SingleInstance";
    private const string ShowEventName = @"Local\MonitorAlign.UTwelve.WinUI3.Show";

    /// <summary>互斥体挂字段：一旦被 GC 回收，第二个实例就能进来。</summary>
    private static Mutex _mutex;
    private static EventWaitHandle _showEvent;

    /// <summary>抢名号。返回 true = 我是第一个。</summary>
    public static bool Claim()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew) return false;

        // 唤醒通道：藏在托盘时进程没有可见主窗，ShowWindow 摸不到它，
        // 只能让对方自己把窗口亮出来。
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        return true;
    }

    /// <summary>通知已存在的实例亮窗口。第二个实例走完这个就退出。</summary>
    public static void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var evt))
            {
                evt.Set();
                evt.Dispose();
                return;
            }
        }
        catch { }

        // 兜底：对方还有可见主窗时，直接把它提到前台
        try
        {
            int me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                if (p.Id == me || p.MainWindowHandle == IntPtr.Zero) continue;
                Win32Overlay.ShowWindow(p.MainWindowHandle, Win32Overlay.SW_RESTORE);
                Win32Overlay.SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
        catch { }
    }

    /// <summary>
    /// 后台线程等唤醒请求。<c>onShow</c> 会被丢回 UI 线程执行
    /// （WaitOne 阻塞在后台线程，UI 线程不该等）。
    /// </summary>
    public static void StartListening(Action onShow)
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        new Thread(() =>
        {
            try
            {
                while (_showEvent.WaitOne())
                {
                    if (queue != null) queue.TryEnqueue(() => onShow());
                    else onShow();
                }
            }
            catch { /* 退出时句柄被释放，安静收场 */ }
        })
        { IsBackground = true }.Start();
    }
}
