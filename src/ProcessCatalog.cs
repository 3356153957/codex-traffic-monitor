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
}

/// <summary>
/// Tracks the Codex desktop application, its CLI, and optionally their descendants.
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
    public void RecordStarted(int pid, int parentId, string name, DateTime start, string? executablePath)
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
            var entry = CreateEntry(info, previous);
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

    private Entry CreateEntry(ProcessInfo info, Entry? previous)
    {
        var sameIdentity = previous is not null && SameIdentity(previous.Info, info);
        if (sameIdentity)
        {
            // Access can briefly fail while a process is initializing or exiting.
            info = info with
            {
                Path = info.Path.Length == 0 ? previous!.Info.Path : info.Path,
                StartedAt = info.StartedAt == DateTime.MinValue ? previous!.Info.StartedAt : info.StartedAt
            };
        }
        var friendlyKind = CoreKind(info.Name, info.Path);
        return new Entry
        {
            Info = info with
            {
                Kind = friendlyKind is null ? "tool" : "core",
                FriendlyKind = friendlyKind ?? "Codex 派生工具"
            },
            Core = friendlyKind is not null,
            Descendant = sameIdentity && previous!.Descendant,
            Excluded = info.Pid == _ownPid || info.ParentId == _ownPid || (sameIdentity && previous!.Excluded),
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
                if (entry.Info.ParentId == entry.Info.Pid ||
                    !_processes.TryGetValue(entry.Info.ParentId, out var parent) ||
                    !CanBeParent(parent.Info, entry.Info)) continue;
                if (parent.Excluded && !entry.Excluded)
                {
                    entry.Excluded = true;
                    changed = true;
                }
                if ((parent.Core || parent.Descendant) && !entry.Descendant)
                {
                    entry.Descendant = true;
                    changed = true;
                }
            }
            if (!changed) break;
        }
    }

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

    private static string? CoreKind(string name, string path)
    {
        var file = FileName(name);
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file += ".exe";
        var normalizedPath = path.Replace('/', '\\');
        var desktopPackage = normalizedPath.Split('\\').Any(part =>
            part.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase));
        if (file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            return desktopPackage ? "Codex 桌面应用" : null;
        if (file.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            return "Codex CLI";

        var trustedHelperPath = desktopPackage ||
            normalizedPath.Contains("\\OpenAI\\Codex\\", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("\\.codex\\", StringComparison.OrdinalIgnoreCase);
        if (!trustedHelperPath) return null;
        var helper = file.Equals("codex-code-mode-host.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-exec-server.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-windows-sandbox.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-windows-sandbox-service.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-sandbox-helper.exe", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("codex-computer-use-swift.exe", StringComparison.OrdinalIgnoreCase);
        return helper ? "Codex 内置助手" : null;
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
            var path = new StringBuilder(32768);
            var capacity = path.Capacity;
            var fullPath = QueryFullProcessImageNameW(process, 0, path, ref capacity) ? path.ToString() : "";
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
            return (fullPath, started);
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
