using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexTraffic;

/// <summary>A process identity. StartedAt is UTC; MinValue means Windows denied the query.</summary>
public sealed record ProcessInfo(
    int Pid,
    int ParentId,
    string Name,
    string Path,
    string Kind,
    DateTime StartedAt)
{
    public string FriendlyKind { get; init; } = "工具进程";
    public TrackedApplication Application { get; init; } = TrackedApplication.Codex;
}

/// <summary>
/// Tracks Codex and Claude desktop applications, their CLIs, and optionally descendants.
/// Thread safe: call Refresh on a worker, not on the WPF dispatcher.
/// </summary>
public sealed class ProcessCatalog
{
    private readonly object _gate = new();
    private readonly object _refreshGate = new();
    private Dictionary<int, Entry> _processes = new();
    private readonly Dictionary<int, long> _stoppedVersions = new();
    private readonly Dictionary<int, List<RetiredEntry>> _recentlyExited = new();
    private readonly Queue<RetiredEntry> _retirementOrder = new();
    private readonly int _ownPid = Environment.ProcessId;
    private long _version;
    private const int MaxRetiredIdentities = 4096;
    private static readonly TimeSpan RetiredRetention = TimeSpan.FromSeconds(15);

    private sealed class Entry
    {
        public required ProcessInfo Info { get; set; }
        public bool Core { get; set; }
        public bool Descendant { get; set; }
        public bool Excluded { get; set; }
        public bool RuntimeCommandLineQueried { get; set; }
        public long EventVersion { get; set; }
    }

    private sealed class RetiredEntry
    {
        public required Entry Process { get; init; }
        public DateTime StoppedAt { get; set; }
        public DateTime RetiredAt { get; init; }
    }

    public void Refresh()
    {
        // Prevent overlapping snapshots from putting an older process identity back.
        lock (_refreshGate)
        {
            long before;
            lock (_gate) before = _version;
            var snapshot = NativeProcesses.ReadSnapshot();
            if (snapshot is null) return; // A failed snapshot must not clear valid state.
            var snapshotAt = DateTime.UtcNow;

            lock (_gate)
            {
                var next = new Dictionary<int, Entry>(snapshot.Count);
                foreach (var info in snapshot)
                {
                    if (_processes.TryGetValue(info.Pid, out var previous) && previous.EventVersion > before)
                    {
                        next[info.Pid] = previous;
                        continue;
                    }
                    if (_stoppedVersions.TryGetValue(info.Pid, out var stopVersion) && stopVersion > before)
                        continue;
                    if (previous is not null && !SameIdentity(previous.Info, info))
                        RetireLocked(previous, info.StartedAt == DateTime.MinValue ? snapshotAt : info.StartedAt);
                    BoundOlderLifetimesLocked(info.Pid, info.StartedAt);
                    next[info.Pid] = CreateEntry(info, previous);
                }

                // Do not lose short-lived ETW starts that happened while enumeration ran.
                foreach (var pair in _processes)
                    if (pair.Value.EventVersion > before)
                        next[pair.Key] = pair.Value;

                foreach (var pair in _processes)
                    if (!next.ContainsKey(pair.Key)) RetireLocked(pair.Value, snapshotAt);

                _processes = next;
                ClassifyLocked();
                PruneRetiredLocked();
                foreach (var pid in _stoppedVersions.Where(p => p.Value <= before).Select(p => p.Key).ToArray())
                    _stoppedVersions.Remove(pid);
            }
        }
    }

    public bool IsTracked(int pid, bool includeChildren = false)
    {
        lock (_gate)
            return _processes.TryGetValue(pid, out var entry) && IsTracked(entry, includeChildren);
    }

    public ProcessInfo? Get(int pid)
    {
        lock (_gate)
            return _processes.TryGetValue(pid, out var entry) ? entry.Info : null;
    }

    /// <summary>
    /// Atomically resolves ownership at the network event's timestamp. Recent exited
    /// identities are retained briefly because ETW delivery can lag process exit.
    /// </summary>
    public ProcessInfo? GetTracked(int pid, bool includeChildren, DateTime eventTime)
    {
        var at = eventTime == DateTime.MinValue ? DateTime.UtcNow : ToUtc(eventTime);
        lock (_gate)
        {
            PruneRetiredLocked();
            Entry? best = null;
            if (_processes.TryGetValue(pid, out var current) &&
                (current.Info.StartedAt == DateTime.MinValue || current.Info.StartedAt <= at) &&
                IsTracked(current, includeChildren)) best = current;

            if (_recentlyExited.TryGetValue(pid, out var exited))
            {
                foreach (var retired in exited)
                {
                    var candidate = retired.Process;
                    if (retired.StoppedAt < at || candidate.Info.StartedAt > at ||
                        !IsTracked(candidate, includeChildren)) continue;
                    if (best is null || candidate.Info.StartedAt > best.Info.StartedAt)
                        best = candidate;
                }
            }
            return best?.Info;
        }
    }

    public IReadOnlyList<ProcessInfo> List(bool includeChildren = false)
    {
        lock (_gate)
            return _processes.Values.Where(p => IsTracked(p, includeChildren))
                .Select(p => p.Info).OrderBy(p => p.Kind).ThenBy(p => p.Name).ThenBy(p => p.Pid).ToArray();
    }

    public void RecordStarted(int pid, int parentId, string name, DateTime start)
        => RecordStarted(pid, parentId, name, start, null);

    /// <summary>Optional path is useful when ETW observes a process that has already exited.</summary>
    public void RecordStarted(int pid, int parentId, string name, DateTime start, string? executablePath, string? commandLine = null)
    {
        if (pid <= 0) return;
        var eventStart = ToUtc(start);
        var queried = NativeProcesses.Query(pid);

        // A delayed start event may refer to the previous owner of a reused PID.
        if (eventStart != DateTime.MinValue && queried.Start != DateTime.MinValue &&
            queried.Start > eventStart.AddMilliseconds(100)) return;

        var path = !string.IsNullOrWhiteSpace(executablePath) ? executablePath! : queried.Path;
        var processName = FileName(name);
        if (string.IsNullOrWhiteSpace(processName) && path.Length != 0) processName = FileName(path);
        if (path.Length == 0 && IsAbsoluteWindowsPath(name)) path = name;
        var startedAt = queried.Start != DateTime.MinValue ? queried.Start : eventStart;
        var info = new ProcessInfo(pid, parentId, processName, path, "tool", startedAt);

        lock (_gate)
        {
            _processes.TryGetValue(pid, out var previous);
            // Start/rundown events can arrive out of order; never replace a newer identity.
            if (previous is not null && previous.Info.StartedAt != DateTime.MinValue &&
                eventStart != DateTime.MinValue && previous.Info.StartedAt > eventStart.AddMilliseconds(100)) return;
            if (previous is not null && !SameIdentity(previous.Info, info))
                RetireLocked(previous, startedAt == DateTime.MinValue ? DateTime.UtcNow : startedAt);
            BoundOlderLifetimesLocked(pid, startedAt);
            var entry = CreateEntry(info, previous, commandLine);
            entry.EventVersion = ++_version;
            _processes[pid] = entry;
            _stoppedVersions.Remove(pid);
            ClassifyLocked();
            PruneRetiredLocked();
        }
    }

    public void RecordStopped(int pid) => RecordStopped(pid, DateTime.UtcNow);

    public void RecordStopped(int pid, DateTime stoppedAt)
    {
        var stoppedUtc = ToUtc(stoppedAt);
        lock (_gate)
        {
            RefineRetiredStopLocked(pid, stoppedUtc);
            if (_processes.TryGetValue(pid, out var process) &&
                process.Info.StartedAt != DateTime.MinValue && stoppedUtc != DateTime.MinValue &&
                stoppedUtc < process.Info.StartedAt) return;
            if (process is not null)
                RetireLocked(process, stoppedUtc == DateTime.MinValue ? DateTime.UtcNow : stoppedUtc);
            _processes.Remove(pid);
            _stoppedVersions[pid] = ++_version;
            PruneRetiredLocked();
            // Existing children retain their birth-time ownership after the parent exits.
        }
    }

    private void RetireLocked(Entry process, DateTime stoppedAt)
    {
        var retired = new RetiredEntry { Process = process, StoppedAt = stoppedAt, RetiredAt = DateTime.UtcNow };
        if (!_recentlyExited.TryGetValue(process.Info.Pid, out var list))
            _recentlyExited[process.Info.Pid] = list = new List<RetiredEntry>();
        list.Add(retired);
        _retirementOrder.Enqueue(retired);
    }

    private void RefineRetiredStopLocked(int pid, DateTime stoppedAt)
    {
        if (stoppedAt == DateTime.MinValue || !_recentlyExited.TryGetValue(pid, out var exited)) return;
        // Refresh may have noticed exit before the actual ETW stop was delivered.
        // Tighten that approximate lifetime without touching a newer PID owner.
        for (var index = exited.Count - 1; index >= 0; index--)
        {
            var retired = exited[index];
            if (retired.Process.Info.StartedAt > stoppedAt) continue;
            if (stoppedAt < retired.StoppedAt) retired.StoppedAt = stoppedAt;
            return;
        }
    }

    private void BoundOlderLifetimesLocked(int pid, DateTime newerStart)
    {
        if (newerStart == DateTime.MinValue || !_recentlyExited.TryGetValue(pid, out var exited)) return;
        foreach (var retired in exited)
            if (retired.Process.Info.StartedAt < newerStart && retired.StoppedAt >= newerStart)
                retired.StoppedAt = newerStart.AddTicks(-1);
    }

    private void PruneRetiredLocked()
    {
        var cutoff = DateTime.UtcNow - RetiredRetention;
        while (_retirementOrder.TryPeek(out var oldest) &&
               (_retirementOrder.Count > MaxRetiredIdentities || oldest.RetiredAt < cutoff))
        {
            _retirementOrder.Dequeue();
            var pid = oldest.Process.Info.Pid;
            if (!_recentlyExited.TryGetValue(pid, out var entries)) continue;
            entries.Remove(oldest);
            if (entries.Count == 0) _recentlyExited.Remove(pid);
        }
    }

    private static bool IsTracked(Entry entry, bool includeChildren)
        => !entry.Excluded && (entry.Core || (includeChildren && entry.Descendant));

    private Entry CreateEntry(ProcessInfo info, Entry? previous, string? commandLine = null)
    {
        var sameIdentity = previous is not null && SameIdentity(previous.Info, info);
        if (sameIdentity)
        {
            // Access can briefly fail while a process is initializing or exiting.
            info = info with
            {
                Path = info.Path.Length == 0 ? previous!.Info.Path : info.Path,
                StartedAt = info.StartedAt == DateTime.MinValue ? previous!.Info.StartedAt : info.StartedAt,
                Application = previous!.Info.Application
            };
        }
        var identity = CoreIdentity(info.Name, info.Path, commandLine);
        var hasCommandLine = !string.IsNullOrWhiteSpace(commandLine);
        var queriedRuntime = sameIdentity && previous!.RuntimeCommandLineQueried;
        if (identity is null && IsNodeRuntime(info.Name) && !queriedRuntime && !hasCommandLine)
        {
            // Older npm installations run cli.js inside node.exe. Read only a
            // candidate runtime once per process identity, and retain no command
            // line or arguments in ProcessInfo, session files, or diagnostics.
            var runtime = NativeProcesses.ReadRuntimeCommandLine(info.Pid);
            if (info.StartedAt == DateTime.MinValue || runtime.Start == DateTime.MinValue || info.StartedAt == runtime.Start)
                identity = CoreIdentity(info.Name, info.Path, runtime.CommandLine);
            queriedRuntime = true;
        }
        if (identity is null && sameIdentity && previous!.Core)
            identity = new CoreProcessIdentity(previous.Info.Application, previous.Info.FriendlyKind);
        var application = identity?.Application ?? info.Application;
        return new Entry
        {
            Info = info with
            {
                Kind = identity is null ? "tool" : "core",
                FriendlyKind = identity?.FriendlyKind ?? DescendantKind(application),
                Application = application
            },
            Core = identity is not null,
            Descendant = sameIdentity && previous!.Descendant,
            Excluded = info.Pid == _ownPid || info.ParentId == _ownPid || (sameIdentity && previous!.Excluded),
            RuntimeCommandLineQueried = queriedRuntime || hasCommandLine,
            EventVersion = sameIdentity ? previous!.EventVersion : 0
        };
    }

    private void ClassifyLocked()
    {
        // Parent PIDs alone are insufficient: Windows reuses them after a process exits.
        // A parent created after its alleged child cannot establish an ownership link.
        for (var pass = 0; pass < _processes.Count; pass++)
        {
            var changed = false;
            foreach (var entry in _processes.Values)
            {
                if (entry.Info.ParentId == entry.Info.Pid) continue;
                var parent = ParentAtBirthLocked(entry.Info);
                if (parent is null) continue;
                if (parent.Excluded && !entry.Excluded)
                {
                    entry.Excluded = true;
                    changed = true;
                }
                if ((parent.Core || parent.Descendant) &&
                    (!entry.Descendant || (!entry.Core && entry.Info.Application != parent.Info.Application)))
                {
                    entry.Descendant = true;
                    // A nested CLI is a new application root: Claude launched
                    // by Codex and Codex launched by Claude each own their own
                    // descendants. Every PID has exactly one application owner.
                    if (!entry.Core)
                        entry.Info = entry.Info with
                        {
                            Application = parent.Info.Application,
                            FriendlyKind = DescendantKind(parent.Info.Application)
                        };
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

    private Entry? ParentAtBirthLocked(ProcessInfo child)
    {
        if (_processes.TryGetValue(child.ParentId, out var parent) && CanBeParent(parent.Info, child))
            return parent;
        // A short-lived parent's stop may be delivered before its child's start.
        // Resolve only a bounded lifetime containing the known child birth time;
        // an unknown timestamp must never attach a reused PID to an old owner.
        if (child.StartedAt == DateTime.MinValue || !_recentlyExited.TryGetValue(child.ParentId, out var retired)) return null;
        Entry? best = null;
        foreach (var item in retired)
            if (item.Process.Info.StartedAt != DateTime.MinValue && item.Process.Info.StartedAt <= child.StartedAt &&
                child.StartedAt <= item.StoppedAt && (best is null || item.Process.Info.StartedAt > best.Info.StartedAt))
                best = item.Process;
        return best;
    }

    private static string DescendantKind(TrackedApplication application)
        => application == TrackedApplication.Claude ? "Claude 派生工具" : "Codex 派生工具";

    private static bool CanBeParent(ProcessInfo parent, ProcessInfo child)
        => parent.StartedAt == DateTime.MinValue || child.StartedAt == DateTime.MinValue ||
           parent.StartedAt <= child.StartedAt;

    private static bool SameIdentity(ProcessInfo previous, ProcessInfo current)
    {
        if (previous.Pid != current.Pid) return false;
        if (previous.StartedAt != DateTime.MinValue && current.StartedAt != DateTime.MinValue)
            return previous.StartedAt == current.StartedAt;
        // Best possible fallback for protected processes whose creation time is unavailable.
        return previous.ParentId == current.ParentId &&
            string.Equals(previous.Name, current.Name, StringComparison.OrdinalIgnoreCase) &&
            (previous.Path.Length == 0 || current.Path.Length == 0 ||
             string.Equals(previous.Path, current.Path, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CoreProcessIdentity(TrackedApplication Application, string FriendlyKind);

    private static CoreProcessIdentity? CoreIdentity(string name, string path, string? commandLine)
    {
        var file = FileName(name);
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file += ".exe";
        // Reject unrelated names before path normalization/splitting. The catalog
        // visits every Windows process on refresh, while only a few can be roots.
        if (file.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            return new(TrackedApplication.Codex, "Codex CLI");
        if (IsNodeRuntime(file))
        {
            if (NodeScript(commandLine) is { } script)
            {
                if (script.EndsWith(@"\node_modules\@anthropic-ai\claude-code\cli.js", StringComparison.OrdinalIgnoreCase))
                    return new(TrackedApplication.Claude, "Claude Code CLI");
                if (script.EndsWith(@"\node_modules\@openai\codex\bin\codex.js", StringComparison.OrdinalIgnoreCase))
                    return new(TrackedApplication.Codex, "Codex CLI");
            }
            return null;
        }
        if (!file.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) && !IsClaudeHelper(file) &&
            !file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) && !IsCodexHelper(file)) return null;
        var normalizedPath = NormalizeImagePath(path);
        if (file.Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (IsClaudeDesktopPath(normalizedPath)) return new(TrackedApplication.Claude, "Claude 桌面应用");
            if (IsClaudeCliPath(normalizedPath)) return new(TrackedApplication.Claude, "Claude Code CLI");
            return null;
        }
        if (IsClaudeDesktopPath(normalizedPath) && IsClaudeHelper(file))
            return new(TrackedApplication.Claude, "Claude 内置助手");
        var desktopPackage = normalizedPath.Split('\\').Any(part =>
            part.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase));
        if (file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            return desktopPackage ? new(TrackedApplication.Codex, "Codex 桌面应用") : null;
        var trustedHelperPath = desktopPackage ||
            normalizedPath.Contains("\\OpenAI\\Codex\\", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("\\.codex\\", StringComparison.OrdinalIgnoreCase);
        if (!trustedHelperPath) return null;
        return IsCodexHelper(file) ? new(TrackedApplication.Codex, "Codex 内置助手") : null;
    }

    private static bool IsCodexHelper(string file)
        => file.Equals("codex-code-mode-host.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-exec-server.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-windows-sandbox.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-windows-sandbox-service.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-sandbox-helper.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-computer-use-swift.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaudeDesktopPath(string path)
    {
        if (!IsAbsoluteWindowsPath(path)) return false;
        var parts = path.Split('\\');
        for (var i = 1; i + 2 < parts.Length; i++)
            if (parts[i - 1].Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) &&
                parts[i].StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) &&
                parts[i].EndsWith("__pzs8sxrjxfjjc", StringComparison.OrdinalIgnoreCase) &&
                parts[i + 1].Equals("app", StringComparison.OrdinalIgnoreCase)) return true;
        var legacyRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\') + @"\AnthropicClaude\";
        if (!path.StartsWith(legacyRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = path[legacyRoot.Length..];
        if (!remainder.Contains('\\')) return true;
        var first = remainder.Split('\\', 2)[0];
        return first.StartsWith("app-", StringComparison.OrdinalIgnoreCase) && Version.TryParse(first[4..], out _);
    }

    private static bool IsClaudeCliPath(string path)
    {
        if (!IsAbsoluteWindowsPath(path) || !FileName(path).Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (path.Equals(home + @"\.local\bin\claude.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(home + @"\.local\share\claude\versions\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(@"\node_modules\@anthropic-ai\claude-code\bin\claude.exe", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var platform in new[] { "win32-x64", "win32-arm64" })
            if (path.EndsWith(@"\node_modules\@anthropic-ai\claude-code-" + platform + @"\claude.exe", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(@"\node_modules\@anthropic-ai\claude-code-" + platform + @"\bin\claude.exe", StringComparison.OrdinalIgnoreCase)) return true;
        var winget = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\') + @"\Microsoft\WinGet\Packages\Anthropic.ClaudeCode_";
        if (path.StartsWith(winget, StringComparison.OrdinalIgnoreCase) && path[winget.Length..].Contains('\\')) return true;
        // The official extension bundles a native Claude Code binary. Avoid
        // matching arbitrary extensions or a parent editor's other extensions.
        var extension = @"\.vscode\extensions\anthropic.claude-code-";
        var extensionAt = path.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
        return extensionAt >= 0 && path.IndexOf(@"\resources\native-binary\claude.exe", extensionAt + extension.Length, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsClaudeHelper(string file)
        => file.Equals("chrome-native-host.exe", StringComparison.OrdinalIgnoreCase) ||
           file.Equals("claude-make-data-folder.exe", StringComparison.OrdinalIgnoreCase) ||
           file.Equals("claude-ssh-askpass.exe", StringComparison.OrdinalIgnoreCase) ||
           file.Equals("claude-ssh-broker.exe", StringComparison.OrdinalIgnoreCase) ||
           file.Equals("claude-ssh-proxy.exe", StringComparison.OrdinalIgnoreCase) ||
           file.Equals("cowork-svc.exe", StringComparison.OrdinalIgnoreCase) ||
           file.Equals("github-mcp-server.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsNodeRuntime(string name)
        => FileName(name).Equals("node.exe", StringComparison.OrdinalIgnoreCase) ||
           FileName(name).Equals("node", StringComparison.OrdinalIgnoreCase);

    private static string? NodeScript(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Length > 65536) return null;
        var cursor = 0;
        if (!TryReadWindowsArgument(commandLine, ref cursor, out var runtime) || !IsNodeRuntime(runtime) ||
            !TryReadWindowsArgument(commandLine, ref cursor, out var script)) return null;
        if (script == "--" && !TryReadWindowsArgument(commandLine, ref cursor, out script)) return null;
        // Only the actual first script argument establishes ownership. A package
        // name in -e code, a prompt, --require, or later data arguments does not.
        return NormalizeImagePath(script) is { Length: > 0 } normalized ? normalized : null;
    }

    private static string NormalizeImagePath(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (!IsAbsoluteWindowsPath(normalized)) return "";
        try { return System.IO.Path.GetFullPath(normalized); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    private static bool TryReadWindowsArgument(string commandLine, ref int cursor, out string argument)
    {
        argument = "";
        while (cursor < commandLine.Length && char.IsWhiteSpace(commandLine[cursor])) cursor++;
        if (cursor == commandLine.Length) return false;
        var value = new StringBuilder();
        var quoted = false;
        while (cursor < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[cursor])))
        {
            var slashes = 0;
            while (cursor < commandLine.Length && commandLine[cursor] == '\\') { slashes++; cursor++; }
            if (cursor < commandLine.Length && commandLine[cursor] == '"')
            {
                value.Append('\\', slashes / 2);
                if (slashes % 2 != 0) value.Append('"'); else quoted = !quoted;
                cursor++;
            }
            else
            {
                value.Append('\\', slashes);
                if (cursor < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[cursor])))
                    value.Append(commandLine[cursor++]);
            }
        }
        if (quoted) return false;
        argument = value.ToString();
        return argument.Length > 0;
    }

    private static string FileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return System.IO.Path.GetFileName(value.Trim().Trim('"').Replace('/', '\\'));
    }

    private static bool IsAbsoluteWindowsPath(string value)
        => value.Length >= 3 && ((char.IsLetter(value[0]) && value[1] == ':' && value[2] == '\\') ||
                               value.StartsWith("\\\\", StringComparison.Ordinal));

    private static DateTime ToUtc(DateTime time)
        => time == DateTime.MinValue ? DateTime.MinValue :
           time.Kind == DateTimeKind.Utc ? time :
           time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime();

    private static class NativeProcesses
    {
        private const uint SnapshotProcesses = 0x00000002;
        private const uint QueryLimitedInformation = 0x1000;
        private const int ErrorNoMoreFiles = 18;

        public static List<ProcessInfo>? ReadSnapshot()
        {
            using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot.IsInvalid) return null;
            var row = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>(), ExeFile = "" };
            if (!Process32FirstW(snapshot, ref row))
                return Marshal.GetLastWin32Error() == ErrorNoMoreFiles ? new List<ProcessInfo>() : null;
            var result = new List<ProcessInfo>();
            do
            {
                if (row.ProcessId is > 0 and <= int.MaxValue)
                {
                    var pid = (int)row.ProcessId;
                    var detail = Query(pid);
                    result.Add(new ProcessInfo(pid, unchecked((int)row.ParentProcessId), row.ExeFile,
                        detail.Path, "tool", detail.Start));
                }
            } while (Process32NextW(snapshot, ref row));
            return Marshal.GetLastWin32Error() == ErrorNoMoreFiles ? result : null;
        }

        public static (string Path, DateTime Start) Query(int pid)
        {
            using var process = OpenProcess(QueryLimitedInformation, false, pid);
            if (process.IsInvalid) return ("", DateTime.MinValue);
            // Most image paths fit in 512 characters. Allocating the full 32K
            // Windows limit for every PID on every poll creates tens of MB/sec.
            // Retry only an insufficient buffer, retaining support for long paths.
            string fullPath = "";
            for (int size = 512; size <= 32768; size *= 2)
            {
                var path = new StringBuilder(size);
                int capacity = path.Capacity;
                if (QueryFullProcessImageNameW(process, 0, path, ref capacity))
                {
                    fullPath = path.ToString();
                    break;
                }
                if (Marshal.GetLastWin32Error() != 122) break;
            }
            return (fullPath, ReadStart(process));
        }

        public static (string CommandLine, DateTime Start) ReadRuntimeCommandLine(int pid)
        {
            using var process = OpenProcess(QueryLimitedInformation, false, pid);
            if (process.IsInvalid) return ("", DateTime.MinValue);
            var started = ReadStart(process);
            // ProcessCommandLineInformation returns one UNICODE_STRING followed
            // by the UTF-16 text. Keep the native and managed allocations bounded
            // and reject any returned pointer outside that same allocation.
            const int processCommandLineInformation = 60;
            int required;
            try { _ = NtQueryInformationProcess(process, processCommandLineInformation, IntPtr.Zero, 0, out required); }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return ("", started); }
            if (required < Marshal.SizeOf<NativeUnicodeString>() || required > 256 * 1024) return ("", started);
            var buffer = Marshal.AllocHGlobal(required);
            try
            {
                if (NtQueryInformationProcess(process, processCommandLineInformation, buffer, required, out _) < 0) return ("", started);
                var text = Marshal.PtrToStructure<NativeUnicodeString>(buffer);
                var offset = text.Buffer.ToInt64() - buffer.ToInt64();
                if (text.Length == 0 || text.Length % 2 != 0 || offset < Marshal.SizeOf<NativeUnicodeString>() ||
                    offset > required - text.Length) return ("", started);
                return (Marshal.PtrToStringUni(text.Buffer, text.Length / 2) ?? "", started);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static DateTime ReadStart(SafeProcessHandle process)
        {
            var started = DateTime.MinValue;
            if (GetProcessTimes(process, out var creation, out _, out _, out _))
            {
                var fileTime = unchecked((long)(((ulong)creation.High << 32) | creation.Low));
                if (fileTime > 0)
                {
                    try { started = DateTime.FromFileTimeUtc(fileTime); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
            return started;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime { public uint Low; public uint High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeUnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
            IntPtr information, int informationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime created,
            out NativeFileTime exited, out NativeFileTime kernel, out NativeFileTime user);
    }
}

/// <summary>Connection metadata only; the socket table contains no byte counters.</summary>
public sealed record ConnectionInfo(
    int Pid,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string Protocol,
    string State);

public static class ConnectionReader
{
    private const uint InsufficientBuffer = 122;
    private const uint TcpTableOwnerPidAll = 5;
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;

    public static IReadOnlyList<ConnectionInfo> GetTcpConnections()
    {
        var connections = new List<ConnectionInfo>();
        ReadTable(AfInet, connections);
        ReadTable(AfInet6, connections);
        return connections;
    }

    private static void ReadTable(uint family, List<ConnectionInfo> output)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
        if (result != InsufficientBuffer || size < sizeof(uint)) return;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Defensive cap: a corrupt size must never cause an unbounded allocation.
            if (size < sizeof(uint) || size > 64 * 1024 * 1024) return;
            var allocated = size;
            var buffer = Marshal.AllocHGlobal(allocated);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
                if (result == InsufficientBuffer) continue;
                if (result != 0) return; // IPv6 may be disabled or unsupported.
                var rowSize = family == AfInet ? Marshal.SizeOf<TcpRow4>() : Marshal.SizeOf<TcpRow6>();
                var count = unchecked((uint)Marshal.ReadInt32(buffer));
                var capacity = (allocated - sizeof(uint)) / rowSize;
                if (count > capacity) return;
                for (var index = 0; index < (int)count; index++)
                {
                    var address = IntPtr.Add(buffer, sizeof(uint) + index * rowSize);
                    if (family == AfInet)
                    {
                        var row = Marshal.PtrToStructure<TcpRow4>(address);
                        output.Add(new ConnectionInfo(unchecked((int)row.Pid),
                            Ipv4(row.LocalAddress), Port(row.LocalPort),
                            Ipv4(row.RemoteAddress), Port(row.RemotePort), "TCP", State(row.State)));
                    }
                    else
                    {
                        var row = Marshal.PtrToStructure<TcpRow6>(address);
                        output.Add(new ConnectionInfo(unchecked((int)row.Pid),
                            new IPAddress(row.LocalAddress, row.LocalScopeId).ToString(), Port(row.LocalPort),
                            new IPAddress(row.RemoteAddress, row.RemoteScopeId).ToString(), Port(row.RemotePort),
                            "TCP", State(row.State)));
                    }
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static string Ipv4(uint address) => new IPAddress(BitConverter.GetBytes(address)).ToString();
    private static int Port(uint networkPort) => (int)(((networkPort & 0xff) << 8) | ((networkPort >> 8) & 0xff));
    private static string State(uint state) => state is >= 1 and <= 12 ? ((TcpState)state).ToString() : "Unknown";

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow4
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint Pid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
}
