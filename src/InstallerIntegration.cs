using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

internal sealed class InstallerState
{
    public int FormatVersion { get; set; } = 1;
    public string OwnerSid { get; set; } = "";
    public string InstallationDirectory { get; set; } = "";
    public bool Prepared { get; set; }
    public DateTime PreparedUtc { get; set; }
    public bool OriginalStartupEnabled { get; set; }
    public string OriginalExecutablePath { get; set; } = "";
    public bool Completed { get; set; }
    public string Error { get; set; } = "";
}

// Helpers never remove traffic history or terminate a process forcibly.
public static class InstallerIntegration
{
    public static int Run(string? operation, string? installationPath, string? statePath)
    {
        InstallerState? state = null;
        string? stateFile = null;
        try
        {
            if (operation is not ("prepare" or "complete" or "uninstall")) throw new ArgumentException("未知安装维护动作。");
            string directory = ValidateInstallationPath(installationPath);
            stateFile = ValidateStatePath(statePath);
            string executable = Path.Combine(directory, "CodexTrafficMonitor.exe");
            if (operation != "prepare" && !SamePath(Environment.ProcessPath, executable))
                throw new ArgumentException("请由目标安装目录中的程序执行安装完成或卸载检查。");

            if (operation == "complete")
            {
                if (!File.Exists(stateFile) || new FileInfo(stateFile).Length > 16384) throw new ArgumentException("安装准备状态不存在或格式异常。");
                state = JsonSerializer.Deserialize<InstallerState>(File.ReadAllText(stateFile)) ?? throw new ArgumentException("安装准备状态无效。");
                ValidatePreparedState(state, directory, StartupManager.UserSid, DateTime.UtcNow);
                if (!state.OriginalStartupEnabled) { state.Completed = true; SaveState(stateFile, state); return 0; }
                var before = StartupManager.ReadStatus();
                if (before.Owned && before.Enabled && SamePath(before.ExecutablePath, executable))
                { state.Completed = true; state.Error = ""; SaveState(stateFile, state); return 0; }
                if (!before.Owned || !before.Enabled || before.Error.Length > 0 || !SamePath(before.ExecutablePath, state.OriginalExecutablePath))
                    return Fail(stateFile, state, 4, "自动启动已在安装期间更改，未覆盖现有设置。可在监控设置中重新开启。");
                var after = StartupManager.SetEnabledAsync(true, state.OriginalExecutablePath).GetAwaiter().GetResult();
                if (after.Error.Length > 0 || !after.Enabled || !after.Owned || !SamePath(after.ExecutablePath, executable))
                    return Fail(stateFile, state, 4, after.Error.Length > 0 ? after.Error : "自动启动迁移核验未通过，请在监控设置中重新开启。");
                state.Completed = true; state.Error = ""; SaveState(stateFile, state); return 0;
            }

            state = new() { OwnerSid = StartupManager.UserSid, InstallationDirectory = directory, PreparedUtc = DateTime.UtcNow };
            SaveState(stateFile, state); // A failed or interrupted preparation is never ready.
            if (operation == "prepare")
            {
                var startup = StartupManager.ReadStatus();
                if (startup.Error.Length > 0 && (!startup.Exists || startup.Owned)) return Fail(stateFile, state, 1, startup.Error);
                state.OriginalStartupEnabled = startup.Owned && startup.Enabled;
                state.OriginalExecutablePath = state.OriginalStartupEnabled ? startup.ExecutablePath : "";
                if (!SingleInstance.RequestMaintenanceExit(null, out string error)) return Fail(stateFile, state, 3, error);
                state.Prepared = true; state.PreparedUtc = DateTime.UtcNow; SaveState(stateFile, state); return 0;
            }

            // An uninstaller must leave a running portable copy in another directory alone.
            if (!SingleInstance.RequestMaintenanceExit(executable, out string exitError)) return Fail(stateFile, state, 3, exitError);
            var current = StartupManager.ReadStatus();
            if (ShouldRemoveStartup(current, executable))
            {
                var after = StartupManager.SetEnabledAsync(false, executable).GetAwaiter().GetResult();
                if (after.Error.Length > 0 || ShouldRemoveStartup(after, executable))
                    return Fail(stateFile, state, 4, after.Error.Length > 0 ? after.Error : "未能移除当前安装的自动启动设置。");
            }
            else if (current.Error.Length > 0 && !current.Exists) return Fail(stateFile, state, 1, current.Error);
            state.Completed = true; SaveState(stateFile, state); return 0;
        }
        catch (Exception e) when (e is ArgumentException or JsonException or NotSupportedException)
        { if (stateFile != null && state != null) TrySaveError(stateFile, state, e.Message); return 2; }
        catch (Exception e)
        { if (stateFile != null && state != null) TrySaveError(stateFile, state, e.Message); return 1; }
    }

    internal static bool ShouldRemoveStartup(StartupStatus status, string executable) => status.Exists && status.Owned && SamePath(status.ExecutablePath, executable);

    internal static void ValidatePreparedState(InstallerState state, string directory, string sid, DateTime now)
    {
        if (state.FormatVersion != 1 || state.OwnerSid != sid || !state.Prepared || !SamePath(state.InstallationDirectory, directory)
            || state.PreparedUtc.Kind != DateTimeKind.Utc || state.PreparedUtc > now.AddMinutes(5) || state.PreparedUtc < now.AddDays(-1))
            throw new ArgumentException("安装准备状态的用户、路径或有效期不匹配，请重新运行安装程序。");
        if (state.OriginalStartupEnabled && (!IsProductExecutable(state.OriginalExecutablePath) || !Path.IsPathFullyQualified(state.OriginalExecutablePath)))
            throw new ArgumentException("原自动启动路径无效。");
        if (state.OriginalExecutablePath == null || (!state.OriginalStartupEnabled && state.OriginalExecutablePath.Length != 0)) throw new ArgumentException("安装准备状态不一致。");
    }

    internal static string ValidateInstallationPath(string? path)
    {
        string result = FullLocalPath(path);
        if (SamePath(result, Path.GetPathRoot(result)) || IsWithin(result, StartupManager.DataDirectory) || IsWithin(StartupManager.DataDirectory, result)
            || IsWithin(result, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            throw new ArgumentException("安装目录不能是磁盘根目录、Windows 系统目录或流量历史数据目录。");
        RejectReparsePoints(result);
        return result;
    }

    internal static string ValidateStatePath(string? path)
    {
        string result = FullLocalPath(path);
        if (!IsWithin(result, Path.GetTempPath()) || !Path.GetExtension(result).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("维护状态必须是当前用户临时目录中的 JSON 文件。");
        RejectReparsePoints(result);
        return result;
    }

    internal static bool SamePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static bool IsWithin(string path, string parent) => SamePath(path, parent) || Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static bool IsProductExecutable(string? path) => string.Equals(Path.GetFileName(path), "CodexTrafficMonitor.exe", StringComparison.OrdinalIgnoreCase);

    private static string FullLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.Contains('"') || path.Any(char.IsControl)
            || path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("维护操作需要完整的本机路径。");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void RejectReparsePoints(string path)
    {
        string? current = path;
        while (current != null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("维护路径不能经过链接或重解析目录。");
            current = Path.GetDirectoryName(current);
        }
    }

    private static int Fail(string path, InstallerState state, int code, string error) { state.Error = error; SaveState(path, state); return code; }
    private static void TrySaveError(string path, InstallerState state, string error) { try { state.Error = error; SaveState(path, state); } catch { } }
    private static void SaveState(string path, InstallerState state)
    {
        ValidateStatePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

internal sealed class MaintenanceMessage
{
    public int Version { get; set; } = 1;
    public string Command { get; set; } = "";
    public string ExpectedExecutable { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string OwnerSid { get; set; } = "";
    public int ProcessId { get; set; }
    public bool Accepted { get; set; }
}

// Kept separate from the old four-byte "show" protocol. Old versions cannot
// accidentally interpret an installation request as permission to stop.
internal static class MaintenanceChannel
{
    internal static async Task ServeAsync(string pipeName, string executable, string sid, Action requestExit, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(2500);
                var request = await ReadAsync(pipe, timeout.Token);
                bool exit = request.Version == 1 && request.Command == "exit" && InstallerIntegration.SamePath(request.ExpectedExecutable, executable);
                bool accepted = request.Version == 1 && (request.Command == "status" || exit);
                await WriteAsync(pipe, new() { Accepted = accepted, ExecutablePath = executable, OwnerSid = sid, ProcessId = Environment.ProcessId }, timeout.Token);
                if (exit) requestExit();
            }
            catch (OperationCanceledException) { if (stop.IsCancellationRequested) break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
    }

    internal static MaintenanceMessage Request(string pipeName, MaintenanceMessage message)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId)) throw new IOException("无法验证维护通道。");
        WriteAsync(pipe, message, timeout.Token).GetAwaiter().GetResult();
        var response = ReadAsync(pipe, timeout.Token).GetAwaiter().GetResult();
        if (response.Version != 1 || response.ProcessId != processId || response.OwnerSid != StartupManager.UserSid
            || !InstallerIntegration.IsProductExecutable(response.ExecutablePath) || !InstallerIntegration.SamePath(ProcessImagePath((int)processId), response.ExecutablePath))
            throw new IOException("维护通道的进程身份不匹配。");
        return response;
    }

    internal static string ProcessImagePath(int processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) throw new IOException("无法读取采集程序路径。");
        try { var buffer = new StringBuilder(32768); int length = buffer.Capacity; if (!QueryFullProcessImageName(handle, 0, buffer, ref length)) throw new IOException("无法读取采集程序路径。"); return buffer.ToString(); }
        finally { CloseHandle(handle); }
    }

    private static async Task<MaintenanceMessage> ReadAsync(Stream stream, CancellationToken stop)
    {
        byte[] lengthBuffer = new byte[4]; await stream.ReadExactlyAsync(lengthBuffer, stop);
        int length = BitConverter.ToInt32(lengthBuffer);
        if (length < 2 || length > 8192) throw new InvalidDataException("维护消息大小无效。");
        byte[] content = new byte[length]; await stream.ReadExactlyAsync(content, stop);
        return JsonSerializer.Deserialize<MaintenanceMessage>(content) ?? throw new InvalidDataException("维护消息无效。");
    }

    private static async Task WriteAsync(Stream stream, MaintenanceMessage message, CancellationToken stop)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), stop); await stream.WriteAsync(bytes, stop); await stream.FlushAsync(stop);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
