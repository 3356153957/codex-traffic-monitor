using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;

namespace CodexTraffic;

// The lock also covers different copies of the app, to avoid duplicate ETW sessions.
public sealed class SingleInstance : IDisposable
{
    private const int RestoreMessage = 0x8000 + 31;
    private readonly FileStream _lock;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    private MainWindow? _window;
    private bool _showRequested;
    private bool _exitRequested;

    private SingleInstance(FileStream file)
    {
        _lock = file;
        _pipeName = PipeName;
        _ = Task.Run(Listen);
        _ = Task.Run(() => MaintenanceChannel.ServeAsync(MaintenancePipeName, Environment.ProcessPath!, StartupManager.UserSid, () =>
        {
            _exitRequested = true;
            _window?.Dispatcher.BeginInvoke(new Action(() => _window.RequestExit()));
        }, _stop.Token));
    }

    private static string PipeName => "CodexTrafficMonitor-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StartupManager.UserSid)))[..20];
    private static string MaintenancePipeName => PipeName + "-maintenance-v1";

    public static SingleInstance? TryCreate()
    {
        Directory.CreateDirectory(StartupManager.DataDirectory);
        try { return new(new FileStream(Path.Combine(StartupManager.DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }

    public void Attach(MainWindow window)
    {
        _window = window;
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            ChangeWindowMessageFilterEx(handle, RestoreMessage, 1, IntPtr.Zero);
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (message == RestoreMessage) { window.RestoreFromTray(); handled = true; }
                return IntPtr.Zero;
            });
        };
        window.Loaded += (_, _) => ApplyPendingRequests();
    }

    internal void ApplyPendingRequests()
    {
        if (_exitRequested) _window?.RequestExit();
        else if (_showRequested)
        {
            _showRequested = false;
            _window?.RestoreFromTray();
        }
    }

    private async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                byte[] message = new byte[4];
                await pipe.ReadExactlyAsync(message, _stop.Token);
                if (Encoding.ASCII.GetString(message) == "show")
                {
                    _showRequested = true;
                    _window?.Dispatcher.BeginInvoke(new Action(ApplyPendingRequests));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { break; }
        }
    }

    public static void ShowExisting()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(500);
            pipe.Write(Encoding.ASCII.GetBytes("show"));
            return;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { }
        IntPtr window = FindWindow(null, "Codex · Claude 流量统计");
        if (window == IntPtr.Zero) window = FindWindow(null, "Codex 流量统计");
        if (window == IntPtr.Zero) window = FindWindow(null, "Codex 流量观察");
        if (window != IntPtr.Zero) SendMessageTimeout(window, RestoreMessage, IntPtr.Zero, IntPtr.Zero, 2, 500, out _);
    }

    internal static bool RequestMaintenanceExit(string? requiredExecutable, out string error)
    {
        error = "请从监控设置或托盘菜单选择“退出程序”，等待记录保存后重试。安装程序不会强制结束采集进程。";
        try
        {
            if (!HasRunningInstance()) { error = ""; return true; }
            MaintenanceMessage running;
            try { running = MaintenanceChannel.Request(MaintenancePipeName, new() { Command = "status" }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A legacy portable instance has no maintenance protocol. During
                // uninstall, only an exact installed path can block file removal.
                if (requiredExecutable != null && !HasOtherProcessAtPath(requiredExecutable)) { error = ""; return true; }
                return false;
            }
            if (!running.Accepted) return false;
            if (requiredExecutable != null && !InstallerIntegration.SamePath(requiredExecutable, running.ExecutablePath)) { error = ""; return true; }
            var response = MaintenanceChannel.Request(MaintenancePipeName, new() { Command = "exit", ExpectedExecutable = running.ExecutablePath });
            if (!response.Accepted || response.ProcessId != running.ProcessId) return false;
            try { using var process = Process.GetProcessById(running.ProcessId); if (!process.WaitForExit(20000)) return false; }
            catch (ArgumentException) { }
            if (HasRunningInstance()) return false;
            error = ""; return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static bool HasRunningInstance()
    {
        string path = Path.Combine(StartupManager.DataDirectory, "instance.lock");
        if (!File.Exists(path)) return false;
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return true; }
    }

    private static bool HasOtherProcessAtPath(string executable)
    {
        foreach (var process in Process.GetProcessesByName("CodexTrafficMonitor"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try { if (InstallerIntegration.SamePath(MaintenanceChannel.ProcessImagePath(process.Id), executable)) return true; }
                catch (IOException)
                {
                    try { if (!process.HasExited) return true; }
                    catch (InvalidOperationException) { }
                }
            }
        }
        return false;
    }

    public void Dispose() { _stop.Cancel(); _lock.Dispose(); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int message, int action, IntPtr info);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, IntPtr wp, IntPtr lp, int flags, int timeout, out IntPtr result);
}
