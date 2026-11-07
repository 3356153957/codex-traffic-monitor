using System.IO;
using System.Xml.Linq;

namespace CodexTraffic;

internal static class InstallerIntegrationSelfTests
{
    public static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAILED: " + name); checks.Add(name); }
        bool Rejects(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
        string sid = StartupManager.UserSid;
        string install = Path.Combine(Path.GetTempPath(), "CodexInstallerTests", Guid.NewGuid().ToString("N"));
        string exe = Path.Combine(install, "CodexTrafficMonitor.exe");
        string other = Path.Combine(install + "-portable", "CodexTrafficMonitor.exe");
        string statePath = Path.Combine(install, "maintenance.json");
        Check(InstallerIntegration.SamePath(exe.ToUpperInvariant(), exe) && !InstallerIntegration.SamePath(exe, other), "Installer path comparison is case-insensitive and does not confuse neighboring installations");
        Check(InstallerIntegration.ValidateInstallationPath(install) == install && InstallerIntegration.ValidateStatePath(statePath) == statePath, "Installer accepts absolute local installation and per-user temporary JSON state paths");
        Check(Rejects(() => InstallerIntegration.ValidateInstallationPath("relative")) && Rejects(() => InstallerIntegration.ValidateInstallationPath(@"\\server\share\app"))
            && Rejects(() => InstallerIntegration.ValidateInstallationPath(StartupManager.DataDirectory)) && Rejects(() => InstallerIntegration.ValidateInstallationPath(Path.GetPathRoot(install)))
            && Rejects(() => InstallerIntegration.ValidateInstallationPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows))), "Installer rejects relative, network, drive-root, Windows and history-data destinations");
        Check(Rejects(() => InstallerIntegration.ValidateStatePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "outside-temp.json")))
            && Rejects(() => InstallerIntegration.ValidateStatePath(Path.Combine(install, "state.exe")))
            && Rejects(() => InstallerIntegration.ValidateStatePath(Path.Combine(install, "state:alternate.json"))), "Installer state cannot target arbitrary user files, alternate data streams or a non-JSON extension");
        var state = new InstallerState { OwnerSid = sid, InstallationDirectory = install, Prepared = true, PreparedUtc = DateTime.UtcNow, OriginalStartupEnabled = true, OriginalExecutablePath = other };
        InstallerIntegration.ValidatePreparedState(state, install, sid, DateTime.UtcNow);
        Check(Rejects(() => InstallerIntegration.ValidatePreparedState(state, install, "S-1-5-21-0", DateTime.UtcNow))
            && Rejects(() => InstallerIntegration.ValidatePreparedState(state, install + "-other", sid, DateTime.UtcNow)), "Prepared installer state is bound to the current SID and exact installation path");
        state.Prepared = false;
        Check(Rejects(() => InstallerIntegration.ValidatePreparedState(state, install, sid, DateTime.UtcNow)), "Failed or interrupted preparation cannot complete an installation");
        state.Prepared = true; state.PreparedUtc = DateTime.UtcNow.AddDays(-2);
        Check(Rejects(() => InstallerIntegration.ValidatePreparedState(state, install, sid, DateTime.UtcNow)), "Stale preparation state is rejected");
        state.PreparedUtc = DateTime.UtcNow; state.OriginalExecutablePath = Path.Combine(install, "unrelated.exe");
        Check(Rejects(() => InstallerIntegration.ValidatePreparedState(state, install, sid, DateTime.UtcNow)), "Startup migration only accepts a product executable as its prior target");
        var task = XDocument.Parse(StartupManager.BuildTaskXml(exe, sid));
        Check(StartupManager.MatchesOwnedExecutable(task, exe) && !StartupManager.MatchesOwnedExecutable(task, other), "Conditional startup mutation requires the exact owned executable path");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        task.Descendants(ns + "Source").Single().Value = "unrelated";
        Check(!StartupManager.MatchesOwnedExecutable(task, exe), "Conditional startup mutation rejects a foreign task even when its executable matches");
        Check(InstallerIntegration.ShouldRemoveStartup(new() { Exists = true, Owned = true, ExecutablePath = exe }, exe)
            && !InstallerIntegration.ShouldRemoveStartup(new() { Exists = true, Owned = true, ExecutablePath = other }, exe)
            && !InstallerIntegration.ShouldRemoveStartup(new() { Exists = true, Owned = false, ExecutablePath = exe }, exe), "Uninstall preserves portable-copy and unrelated startup tasks, including disabled tasks");

        // A unique pipe and inert callback ensure these tests never address or stop
        // the real dashboard, its global instance lock, or its capture session.
        string pipe = "CodexTrafficInstallerTest-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        int exitCount = 0;
        string running = Environment.ProcessPath!;
        Task server = Task.Run(() => MaintenanceChannel.ServeAsync(pipe, running, sid, () => Interlocked.Increment(ref exitCount), stop.Token));
        try
        {
            var status = MaintenanceChannel.Request(pipe, new() { Command = "status" });
            Check(status.Accepted && status.ProcessId == Environment.ProcessId && status.OwnerSid == sid && InstallerIntegration.SamePath(status.ExecutablePath, running), "Isolated maintenance IPC verifies the operating-system peer PID, current SID and executable path");
            var wrong = MaintenanceChannel.Request(pipe, new() { Command = "exit", ExpectedExecutable = other });
            Check(!wrong.Accepted && exitCount == 0, "Isolated maintenance IPC refuses to exit an instance in a different directory");
            var invalid = MaintenanceChannel.Request(pipe, new() { Version = 999, Command = "exit", ExpectedExecutable = running });
            Check(!invalid.Accepted && exitCount == 0, "Isolated maintenance IPC rejects unsupported protocol versions");
            var accepted = MaintenanceChannel.Request(pipe, new() { Command = "exit", ExpectedExecutable = running });
            SpinWait.SpinUntil(() => Volatile.Read(ref exitCount) == 1, 1000);
            Check(accepted.Accepted && exitCount == 1, "Isolated maintenance IPC requests a graceful callback only for its exact executable");
        }
        finally { stop.Cancel(); server.GetAwaiter().GetResult(); }
        return checks;
    }
}
