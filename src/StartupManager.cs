using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;

namespace CodexTraffic;

public sealed class CaptureOptions
{
    public bool IncludeChildren { get; set; } = true;
    public bool IncludeLoopback { get; set; } = true;
}

public sealed class StartupStatus
{
    public bool Exists { get; set; }
    public bool Owned { get; set; }
    public bool Enabled { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string Error { get; set; } = "";
}

public static class StartupManager
{
    internal const string TaskSource = "CodexTrafficMonitor.Autostart.v1";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTrafficMonitor");
    public static string UserSid => WindowsIdentity.GetCurrent().User!.Value;
    public static string TaskName => "CodexTrafficMonitor-" + UserSid;
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static readonly object SettingsGate = new();

    public static CaptureOptions LoadCaptureOptions()
    {
        lock (SettingsGate)
        {
            string file = Path.Combine(DataDirectory, "settings.json");
            if (!File.Exists(file)) return new();
            return JsonSerializer.Deserialize<CaptureOptions>(File.ReadAllText(file)) ?? new();
        }
    }

    public static void SaveCaptureOptions(bool children, bool loopback)
    {
        lock (SettingsGate)
        {
            Directory.CreateDirectory(DataDirectory);
            string file = Path.Combine(DataDirectory, "settings.json");
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CaptureOptions { IncludeChildren = children, IncludeLoopback = loopback }));
            File.Move(temporary, file, true);
        }
    }

    public static StartupStatus ReadStatus()
    {
        object? service = null, folder = null, registered = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            try { registered = ((dynamic)folder).GetTask(TaskName); }
            catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x8004130F) { return new() { TaskName = TaskName }; }
            return ParseStatus((string)((dynamic)registered).Xml, (bool)((dynamic)registered).Enabled, UserSid, TaskName);
        }
        catch (Exception e) { return new() { TaskName = TaskName, Error = "无法读取开机设置：" + e.Message }; }
        finally { Release(registered); Release(folder); Release(service); }
    }

    internal static StartupStatus ParseStatus(string xml, bool enabled, string sid, string taskName)
    {
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = document.Root!;
        var principal = root.Element(ns + "Principals")?.Element(ns + "Principal");
        string executable = root.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value ?? "";
        bool owned = root.Element(ns + "RegistrationInfo")?.Element(ns + "Source")?.Value == TaskSource;
        bool correctPrincipal = MatchesUser(principal?.Element(ns + "UserId")?.Value, sid) && principal?.Element(ns + "LogonType")?.Value == "InteractiveToken" && principal?.Element(ns + "RunLevel")?.Value == "HighestAvailable";
        bool correctTrigger = root.Element(ns + "Triggers")?.Elements().Count() == 1 && root.Element(ns + "Triggers")?.Elements(ns + "LogonTrigger").Any(x => MatchesUser(x.Element(ns + "UserId")?.Value, sid) && x.Element(ns + "Enabled")?.Value != "false") == true;
        bool correctAction = root.Element(ns + "Actions")?.Elements().Count() == 1 && root.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Arguments")?.Value == "--start --minimized";
        string error = !owned ? "同名计划任务不属于本程序，请先检查 Windows 任务计划程序。" : !correctPrincipal || !correctTrigger || !correctAction ? "开机任务配置与自动监控要求不一致，请重新开启。" : enabled && !File.Exists(executable) ? "开机任务的程序已移动或被删除，请重新开启更新路径。" : "";
        return new() { Exists = true, Owned = owned, Enabled = enabled && error.Length == 0, ExecutablePath = executable, TaskName = taskName, Error = error };
    }

    public static async Task<StartupStatus> SetEnabledAsync(bool enabled, string? expectedExecutable = null)
    {
        if (IsAdmin) return await Task.Run(() => Configure(enabled, expectedExecutable));
        string request = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(DataDirectory, "startup-requests"));
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--configure-startup");
        info.ArgumentList.Add(enabled ? "on" : "off");
        info.ArgumentList.Add("--owner-sid"); info.ArgumentList.Add(UserSid);
        info.ArgumentList.Add("--request"); info.ArgumentList.Add(request);
        if (expectedExecutable != null) { info.ArgumentList.Add("--expected-executable"); info.ArgumentList.Add(expectedExecutable); }
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("未能启动管理员设置程序。");
            await process.WaitForExitAsync();
            string result = RequestFile(request);
            if (!File.Exists(result)) { var status = ReadStatus(); status.Error = "管理员设置未完成，开机设置未更改。"; return status; }
            var response = JsonSerializer.Deserialize<StartupStatus>(await File.ReadAllTextAsync(result));
            if (response != null) return response;
            var actual = ReadStatus(); actual.Error = "无法读取开机设置结果。"; return actual;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { var status = ReadStatus(); status.Error = "已取消管理员确认，开机设置未更改。"; return status; }
    }

    public static StartupStatus Configure(bool enabled, string? expectedExecutable = null)
    {
        object? service = null, folder = null, registered = null;
        try
        {
            if (!IsAdmin) throw new UnauthorizedAccessException("设置自动监控需要一次管理员确认。");
            string executable = Environment.ProcessPath!;
            if (!Path.GetFileName(executable).Equals("CodexTrafficMonitor.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请从发布的 CodexTrafficMonitor.exe 设置开机监控。");
            var before = ReadStatus();
            if (before.Error.Length > 0 && before.ExecutablePath.Length == 0) throw new InvalidOperationException(before.Error);
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            // Never replace or remove an unrelated task that happens to use our name.
            try
            {
                registered = ((dynamic)folder).GetTask(TaskName);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var current = XDocument.Parse((string)((dynamic)registered).Xml);
                if (expectedExecutable != null && !MatchesOwnedExecutable(current, expectedExecutable))
                {
                    if (enabled) throw new InvalidOperationException("自动启动已在其他位置更改，未覆盖现有设置。");
                    return ReadStatus(); // Uninstall only removes its own exact executable.
                }
                if (current.Root?.Element(ns + "RegistrationInfo")?.Element(ns + "Source")?.Value != TaskSource)
                    throw new InvalidOperationException("同名任务不属于本程序，未修改。");
                Release(registered); registered = null;
            }
            catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x8004130F) { }
            if (enabled && expectedExecutable != null && registered == null)
            {
                var current = ReadStatus();
                if (!current.Exists || !current.Owned || !InstallerIntegration.SamePath(current.ExecutablePath, expectedExecutable))
                    throw new InvalidOperationException("原自动启动设置已移除或更改，未重新开启。");
            }
            if (enabled)
                registered = ((dynamic)folder).RegisterTask(TaskName, BuildTaskXml(executable, UserSid), 6, UserSid, null, 3, null);
            else
            {
                try { ((dynamic)folder).DeleteTask(TaskName, 0); }
                catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x8004130F) { }
            }
            var after = ReadStatus();
            if (after.Error.Length == 0 && after.Enabled != enabled) after.Error = "计划任务设置后核验未通过，请重试。";
            return after;
        }
        catch (Exception e) { var status = ReadStatus(); status.Error = "开机设置失败：" + e.Message; return status; }
        finally { Release(registered); Release(folder); Release(service); }
    }

    public static int ConfigureCommand(bool enabled, string? request, string? ownerSid, string? expectedExecutable = null)
    {
        if (ownerSid != null && ownerSid != UserSid) return 5; // Reject a different account supplied through credential UAC.
        var result = Configure(enabled, expectedExecutable);
        if (request != null)
        {
            Directory.CreateDirectory(Path.Combine(DataDirectory, "startup-requests"));
            File.WriteAllText(RequestFile(request), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        return result.Error.Length == 0 ? 0 : 1;
    }

    private static string RequestFile(string request) => Path.Combine(DataDirectory, "startup-requests", Guid.ParseExact(request, "N").ToString("N") + ".json");

    internal static bool MatchesOwnedExecutable(XDocument document, string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return document.Root?.Element(ns + "RegistrationInfo")?.Element(ns + "Source")?.Value == TaskSource
            && InstallerIntegration.SamePath(document.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value, executable);
    }

    private static bool MatchesUser(string? value, string sid)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (string.Equals(value, sid, StringComparison.OrdinalIgnoreCase)) return true;
        // Task Scheduler canonicalizes LogonTrigger.UserId to COMPUTER\user while
        // leaving Principal.UserId as a SID. Compare identities, not their spelling.
        try { return ((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value == sid; }
        catch (IdentityNotMappedException) { return false; }
        catch (SystemException) { return false; }
    }

    public static string BuildTaskXml(string executable, string sid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object? value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new[] { E("Author", "Codex 流量观察"), E("Description", "当前用户登录 Windows 后，在托盘后台自动记录 Codex 网络上下行；可在程序内关闭。"), E("Source", TaskSource) }),
            E("Triggers", E("LogonTrigger", new[] { E("Enabled", "true"), E("UserId", sid) })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"), E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"), E("AllowHardTerminate", "false"), E("StartWhenAvailable", "true"), E("RunOnlyIfNetworkAvailable", "false"), E("IdleSettings", new[] { E("StopOnIdleEnd", "false"), E("RestartOnIdle", "false") }), E("AllowStartOnDemand", "true"), E("Enabled", "true"), E("Hidden", "false"), E("RunOnlyIfIdle", "false"), E("WakeToRun", "false"), E("ExecutionTimeLimit", "PT0S"), E("Priority", "7") }),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), E("Exec", new[] { E("Command", Path.GetFullPath(executable)), E("Arguments", "--start --minimized"), E("WorkingDirectory", Path.GetDirectoryName(Path.GetFullPath(executable))) })))).ToString();
    }

    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
