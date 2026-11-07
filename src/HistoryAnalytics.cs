using System.IO;
using System.Text.Json;

namespace CodexTraffic;

/// <summary>
/// A durable-history index. Refresh belongs on a worker thread after SessionStore's
/// flush; snapshots never add a live ledger to rows that may already be on disk.
/// Closed sessions are not reread unless their file length or write time changes.
/// </summary>
public sealed class HistoryAnalytics
{
    private const int MaximumLineBytes = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, SessionIndex> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private DateTime _publishedDay;
    private bool _needsPublish = true;
    private string _directoryWarning = "";
    private IReadOnlyDictionary<(DashboardRange Range, DashboardTrafficScope Scope), DashboardSnapshot> _published = Enum.GetValues<DashboardRange>()
        .SelectMany(range => Enum.GetValues<DashboardTrafficScope>().Select(scope => (range, scope)))
        .ToDictionary(key => key, key => new DashboardSnapshot { Range = key.range, TrafficScope = key.scope, IsLoading = true });

    public HistoryAnalytics(string dataDirectory, Func<DateTime>? utcNow = null)
    {
        _directory = Path.Combine(dataDirectory, "sessions");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public void Refresh()
    {
        lock (_gate)
        {
            DateTime now = _utcNow();
            DateTime today = now.ToLocalTime().Date;
            bool changed = _needsPublish || !_loaded || _publishedDay != today;
            string previousWarning = _directoryWarning;
            try
            {
                // Enumerate directly: Exists also returns false on permission errors,
                // which must not masquerade as a successful empty-history refresh.
                var paths = Directory.GetDirectories(_directory);
                var present = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                foreach (string removed in _sessions.Keys.Where(x => !present.Contains(x)).ToList())
                { _sessions.Remove(removed); changed = true; }
                foreach (string path in paths)
                {
                    if (!_sessions.TryGetValue(path, out var index))
                    { _sessions[path] = index = new SessionIndex(path); changed = true; }
                    var before = index.Stamp;
                    ReadMetadata(index);
                    ReadFlows(index);
                    changed |= before != index.Stamp;
                }
                _directoryWarning = "";
            }
            catch (DirectoryNotFoundException) { changed |= _sessions.Count > 0; _sessions.Clear(); _directoryWarning = ""; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _directoryWarning = "无法读取部分历史目录，保留上次成功读取的统计：" + e.Message;
            }
            finally
            {
                _loaded = true;
                if (changed || previousWarning != _directoryWarning)
                {
                    _needsPublish = true;
                    var snapshots = Enum.GetValues<DashboardRange>()
                        .SelectMany(range => Enum.GetValues<DashboardTrafficScope>().Select(scope => (range, scope)))
                        .ToDictionary(key => key, key => BuildSnapshot(key.range, key.scope, now));
                    Volatile.Write(ref _published, snapshots);
                    _publishedDay = today;
                    _needsPublish = false;
                }
            }
        }
    }

    public DashboardSnapshot GetSnapshot(DashboardRange range, DashboardTrafficScope scope = DashboardTrafficScope.All)
    {
        var published = Volatile.Read(ref _published);
        if (!published.TryGetValue((range, scope), out var saved)) saved = published[(DashboardRange.All, DashboardTrafficScope.All)];
        // Protect the index and other windows from callers modifying data-bound rows.
        return new DashboardSnapshot
        {
            Range = saved.Range, TrafficScope = saved.TrafficScope, FromUtc = saved.FromUtc, ToUtc = saved.IsLoading ? saved.ToUtc : _utcNow(), FirstRecordedUtc = saved.FirstRecordedUtc,
            UploadBytes = saved.UploadBytes, DownloadBytes = saved.DownloadBytes,
            AllUploadBytes = saved.AllUploadBytes, AllDownloadBytes = saved.AllDownloadBytes,
            ProxyUploadBytes = saved.ProxyUploadBytes, ProxyDownloadBytes = saved.ProxyDownloadBytes,
            DirectUploadBytes = saved.DirectUploadBytes, DirectDownloadBytes = saved.DirectDownloadBytes,
            UnknownUploadBytes = saved.UnknownUploadBytes, UnknownDownloadBytes = saved.UnknownDownloadBytes,
            TodayUploadBytes = saved.TodayUploadBytes, TodayDownloadBytes = saved.TodayDownloadBytes,
            RecordedDays = saved.RecordedDays, SessionCount = saved.SessionCount, HasRecords = saved.HasRecords,
            IsLoading = saved.IsLoading, Warning = saved.Warning,
            DailyUsage = saved.DailyUsage.Select(d => new DailyUsageRow { Date = d.Date, UploadBytes = d.UploadBytes, DownloadBytes = d.DownloadBytes }).ToList(),
            Targets = saved.Targets.Select(CloneTarget).ToList()
        };
    }

    private DashboardSnapshot BuildSnapshot(DashboardRange range, DashboardTrafficScope scope, DateTime now)
    {
        lock (_gate)
        {
            DateTime today = now.ToLocalTime().Date;
            DateTime? start = range switch
            {
                DashboardRange.Today => today,
                DashboardRange.Last7Days => today.AddDays(-6),
                DashboardRange.Last30Days => today.AddDays(-29),
                _ => null
            };
            var snapshot = new DashboardSnapshot
            {
                Range = range, TrafficScope = scope, FromUtc = start?.ToUniversalTime(), ToUtc = now,
                IsLoading = !_loaded
            };
            var days = new SortedDictionary<DateTime, DailyUsageRow>();
            var targets = new Dictionary<TargetKey, TargetRow>();
            int malformedRows = 0, malformedSessions = 0, metadataErrors = 0, flowErrors = 0;
            int inconsistentSessions = 0, unallocatedSessions = 0, partialTails = 0, lostSessions = 0;
            foreach (var index in _sessions.Values)
            {
                if (index.MetadataError.Length > 0) metadataErrors++;
                if (index.FlowError.Length > 0) flowErrors++;
                if (index.BadLines > 0) { malformedSessions++; malformedRows += index.BadLines; }
                if (index.IncompleteClosedTail) partialTails++;
                var record = index.Record;
                if (record?.EventsLost > 0) lostSessions++;
                DateTime? first = index.FirstTimestampUtc;
                if (record != null && record.StartedUtc != default)
                {
                    DateTime began = AsUtc(record.StartedUtc);
                    if (first == null || began < first) first = began;
                }
                if (first != null && (snapshot.FirstRecordedUtc == null || first < snapshot.FirstRecordedUtc)) snapshot.FirstRecordedUtc = first;

                bool validSession = record != null || index.ValidLines > 0;
                bool hasSelectedDay = index.Days.Any(d => InRange(d.Key, start, today) &&
                    (scope == DashboardTrafficScope.All || d.Value.Targets.Values.Any(t => t.TrafficKind == TrafficKind.Proxy)));
                bool overlaps = record != null && AsUtc(record.StartedUtc).ToLocalTime().Date <= today &&
                    (start == null || AsUtc(record.EndedUtc ?? record.UpdatedUtc).ToLocalTime().Date >= start);
                if (validSession && (hasSelectedDay || scope == DashboardTrafficScope.All && (overlaps || range == DashboardRange.All))) snapshot.SessionCount++;
                snapshot.HasRecords |= validSession;

                long selectedUp = 0, selectedDown = 0;
                foreach (var pair in index.Days)
                {
                    var scopedTargets = scope == DashboardTrafficScope.All ? pair.Value.Targets.Values.ToList()
                        : pair.Value.Targets.Values.Where(t => t.TrafficKind == TrafficKind.Proxy).ToList();
                    long dayUp = scope == DashboardTrafficScope.All ? pair.Value.Upload : scopedTargets.Sum(t => t.UploadBytes);
                    long dayDown = scope == DashboardTrafficScope.All ? pair.Value.Download : scopedTargets.Sum(t => t.DownloadBytes);
                    if (pair.Key == today)
                    {
                        snapshot.TodayUploadBytes += dayUp;
                        snapshot.TodayDownloadBytes += dayDown;
                    }
                    if (!InRange(pair.Key, start, today)) continue;
                    selectedUp += pair.Value.Upload; selectedDown += pair.Value.Download;
                    foreach (var target in pair.Value.Targets)
                    {
                        if (target.Value.TrafficKind == TrafficKind.Proxy)
                        { snapshot.ProxyUploadBytes += target.Value.UploadBytes; snapshot.ProxyDownloadBytes += target.Value.DownloadBytes; }
                        else if (target.Value.TrafficKind == TrafficKind.Direct)
                        { snapshot.DirectUploadBytes += target.Value.UploadBytes; snapshot.DirectDownloadBytes += target.Value.DownloadBytes; }
                    }
                    if (scope == DashboardTrafficScope.Proxy && scopedTargets.Count == 0) continue;
                    if (!days.TryGetValue(pair.Key, out var day)) days[pair.Key] = day = new DailyUsageRow { Date = pair.Key };
                    day.UploadBytes += dayUp; day.DownloadBytes += dayDown;
                    foreach (var target in pair.Value.Targets)
                    {
                        if (scope == DashboardTrafficScope.Proxy && target.Value.TrafficKind != TrafficKind.Proxy) continue;
                        if (!targets.TryGetValue(target.Key, out var aggregate)) targets[target.Key] = aggregate = CloneTarget(target.Value);
                        else MergeTarget(aggregate, target.Value);
                    }
                }

                // session.json is the canonical session counter. If some detail is
                // unreadable it can rescue the all-time number, but we cannot invent
                // the missing day, direction's destination, or process attribution.
                long canonicalUp = Math.Max(index.Upload, Math.Max(0, record?.UploadBytes ?? 0));
                long canonicalDown = Math.Max(index.Download, Math.Max(0, record?.DownloadBytes ?? 0));
                bool mismatch = record != null && (record.UploadBytes != index.Upload || record.DownloadBytes != index.Download);
                if (mismatch) inconsistentSessions++;
                if (canonicalUp > index.Upload || canonicalDown > index.Download) unallocatedSessions++;
                snapshot.AllUploadBytes += range == DashboardRange.All ? canonicalUp : selectedUp;
                snapshot.AllDownloadBytes += range == DashboardRange.All ? canonicalDown : selectedDown;
            }
            snapshot.UnknownUploadBytes = snapshot.AllUploadBytes - snapshot.ProxyUploadBytes - snapshot.DirectUploadBytes;
            snapshot.UnknownDownloadBytes = snapshot.AllDownloadBytes - snapshot.ProxyDownloadBytes - snapshot.DirectDownloadBytes;
            snapshot.UploadBytes = scope == DashboardTrafficScope.Proxy ? snapshot.ProxyUploadBytes : snapshot.AllUploadBytes;
            snapshot.DownloadBytes = scope == DashboardTrafficScope.Proxy ? snapshot.ProxyDownloadBytes : snapshot.AllDownloadBytes;
            snapshot.RecordedDays = days.Count;
            snapshot.DailyUsage = days.Values.ToList();
            snapshot.Targets = targets.Values.OrderByDescending(t => (decimal)t.UploadBytes + t.DownloadBytes).ToList();
            var warnings = new List<string>();
            if (_directoryWarning.Length > 0) warnings.Add(_directoryWarning);
            if (metadataErrors > 0) warnings.Add($"{metadataErrors} 个会话的摘要无法读取，相关统计使用可读明细或上次可用摘要。");
            if (flowErrors > 0) warnings.Add($"{flowErrors} 个会话的流量文件无法读取，明细统计可能不完整。");
            if (malformedRows > 0) warnings.Add($"{malformedSessions} 个会话有 {malformedRows} 条损坏明细，已跳过。");
            if (partialTails > 0) warnings.Add($"{partialTails} 个已结束会话的末尾记录不完整。");
            if (unallocatedSessions > 0)
                warnings.Add(range == DashboardRange.All
                    ? $"{unallocatedSessions} 个会话的累计总量已用会话摘要补足，部分流量无法分配到日期或去向。"
                    : $"{unallocatedSessions} 个会话存在无法分配日期的流量，本时间范围仅统计可读明细，可能偏低。");
            else if (inconsistentSessions > 0) warnings.Add($"{inconsistentSessions} 个会话的摘要与明细暂不一致，已保留较大的已记录总量。");
            if (lostSessions > 0) warnings.Add($"{lostSessions} 个历史会话曾丢失采集事件，记录可能偏低。");
            snapshot.Warning = string.Join(" ", warnings);
            return snapshot;
        }
    }

    private static bool InRange(DateTime day, DateTime? start, DateTime today) => start == null || day <= today && day >= start;
    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    private static void ReadMetadata(SessionIndex index)
    {
        string path = Path.Combine(index.Path, "session.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { index.MetadataError = "会话摘要不存在"; index.Record = null; return; }
            if (index.MetadataLength == info.Length && index.MetadataWriteUtc == info.LastWriteTimeUtc && index.MetadataError.Length == 0) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var record = JsonSerializer.Deserialize<SessionRecord>(stream);
            if (record == null || record.StartedUtc == default || record.UploadBytes < 0 || record.DownloadBytes < 0)
                throw new JsonException("无效的会话摘要");
            index.Record = record;
            index.MetadataLength = info.Length; index.MetadataWriteUtc = info.LastWriteTimeUtc;
            index.MetadataError = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            index.MetadataError = e.Message;
        }
    }

    private static void ReadFlows(SessionIndex index)
    {
        string path = Path.Combine(index.Path, "flows.jsonl");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                if (index.SeenLength >= 0) index.ResetFlows();
                index.FlowError = index.Record is { UploadBytes: > 0 } or { DownloadBytes: > 0 } ? "流量文件不存在" : "";
                return;
            }
            bool closed = index.Record?.EndedUtc != null || index.Record?.Status == "completed";
            if (index.SeenLength == info.Length && index.FlowWriteUtc == info.LastWriteTimeUtc && index.LastReadClosed == closed && index.FlowError.Length == 0) return;
            // Replacement/truncation must rebuild this session, not preserve stale
            // bytes and add a second copy of its rewritten rows.
            if (info.Length < index.SeenLength || info.Length == index.SeenLength && info.LastWriteTimeUtc != index.FlowWriteUtc) index.ResetFlows();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
            long length = stream.Length;
            if (length < index.Offset) index.ResetFlows();
            stream.Position = index.Offset;
            byte[] buffer = new byte[65536];
            using var line = new MemoryStream();
            bool tooLong = false;
            long cursor = index.Offset;
            while (cursor < length)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - cursor));
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                {
                    cursor++;
                    if (buffer[i] == (byte)'\n')
                    {
                        if (tooLong) index.BadLines++;
                        else ReadLine(index, line.GetBuffer().AsSpan(0, (int)line.Length));
                        index.Offset = cursor;
                        line.SetLength(0); tooLong = false;
                    }
                    else if (!tooLong)
                    {
                        if (line.Length >= MaximumLineBytes) { line.SetLength(0); tooLong = true; }
                        else line.WriteByte(buffer[i]);
                    }
                }
            }
            index.IncompleteClosedTail = false;
            if (closed && cursor > index.Offset)
            {
                // A final valid JSON object need not have a line terminator. An
                // incomplete UTF-8/JSON tail stays uncommitted and can be retried.
                if (!tooLong && ReadLine(index, line.GetBuffer().AsSpan(0, (int)line.Length), countFailure: false)) index.Offset = cursor;
                else index.IncompleteClosedTail = true;
            }
            index.SeenLength = length; index.FlowWriteUtc = info.LastWriteTimeUtc;
            index.LastReadClosed = closed; index.FlowError = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            index.FlowError = e.Message;
        }
    }

    private static bool ReadLine(SessionIndex index, ReadOnlySpan<byte> bytes, bool countFailure = true)
    {
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        if (index.Offset == 0 && bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        if (bytes.IsEmpty) return true;
        try
        {
            var flow = JsonSerializer.Deserialize<FlowRecord>(bytes);
            if (flow == null || flow.TimestampUtc == default || flow.UploadBytes < 0 || flow.DownloadBytes < 0) throw new JsonException("无效流量记录");
            DateTime utc = AsUtc(flow.TimestampUtc);
            DateTime date = utc.ToLocalTime().Date;
            long newUp = checked(index.Upload + flow.UploadBytes), newDown = checked(index.Download + flow.DownloadBytes);
            if (!index.Days.TryGetValue(date, out var day)) index.Days[date] = day = new DayIndex();
            day.Upload = checked(day.Upload + flow.UploadBytes); day.Download = checked(day.Download + flow.DownloadBytes);
            var target = ToTarget(flow, utc);
            var key = new TargetKey(target.Process.ToLowerInvariant(), target.Protocol.ToUpperInvariant(), target.Address.ToLowerInvariant(), target.Port,
                target.Hostname.ToLowerInvariant(), target.ProxyDestination.ToLowerInvariant(), target.ProxyNode, target.TrafficKind, target.ProxyType.ToLowerInvariant());
            if (!day.Targets.TryGetValue(key, out var aggregate)) day.Targets[key] = target;
            else MergeTarget(aggregate, target);
            index.Upload = newUp; index.Download = newDown; index.ValidLines++;
            if (index.FirstTimestampUtc == null || utc < index.FirstTimestampUtc) index.FirstTimestampUtc = utc;
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or OverflowException)
        {
            if (countFailure) index.BadLines++;
            return false;
        }
    }

    private static TargetRow ToTarget(FlowRecord flow, DateTime utc)
    {
        string address = flow.RemoteAddress ?? "";
        string host = flow.ProxyReportedHost ?? "";
        string destinationAddress = flow.ProxyReportedAddress ?? "";
        bool proxyReported = host.Length > 0 || destinationAddress.Length > 0;
        var target = new TargetRow
        {
            Address = address, Port = flow.RemotePort, Target = AddressClassifier.Endpoint(address, flow.RemotePort),
            Process = string.IsNullOrEmpty(flow.Process) ? "未知进程" : flow.Process, Protocol = flow.Protocol ?? "",
            Route = flow.Route ?? "", Hostname = host, DomainSource = proxyReported ? "代理报告（记录时）" : "",
            ProxyDestination = proxyReported ? AddressClassifier.Endpoint(destinationAddress.Length > 0 ? destinationAddress : host, flow.ProxyReportedPort) : "",
            ProxyChain = flow.ProxyReportedChain ?? "", ProxyNode = string.IsNullOrEmpty(flow.ProxyNode) ? GeoIpHints.NodeFromChain(flow.ProxyReportedChain ?? "") : flow.ProxyNode,
            ProxyType = flow.ProxyType ?? "",
            Region = flow.Region ?? "", RegionSource = flow.RegionSource ?? "", UploadBytes = flow.UploadBytes, DownloadBytes = flow.DownloadBytes,
            LastSeen = utc.ToLocalTime(), Pid = 0, LocalPort = 0
        };
        TrafficClassifier.Apply(target);
        return target;
    }

    private static TargetRow CloneTarget(TargetRow row) => new()
    {
        Address = row.Address, Port = row.Port, Target = row.Target, Process = row.Process, Protocol = row.Protocol,
        Route = row.Route, Hostname = row.Hostname, DomainSource = row.DomainSource, ProxyDestination = row.ProxyDestination,
        ProxyChain = row.ProxyChain, ProxyNode = row.ProxyNode, Region = row.Region, RegionSource = row.RegionSource,
        ProxyType = row.ProxyType, TrafficKind = row.TrafficKind, TrafficKindSource = row.TrafficKindSource,
        UploadBytes = row.UploadBytes, DownloadBytes = row.DownloadBytes, LastSeen = row.LastSeen
    };

    private static void MergeTarget(TargetRow into, TargetRow from)
    {
        into.UploadBytes = checked(into.UploadBytes + from.UploadBytes);
        into.DownloadBytes = checked(into.DownloadBytes + from.DownloadBytes);
        if (from.LastSeen > into.LastSeen) into.LastSeen = from.LastSeen;
        if (into.Route != from.Route) into.Route = "多种路径";
        if (into.ProxyChain != from.ProxyChain) into.ProxyChain = "多个代理链";
        if (into.Region != from.Region && from.Region.Length > 0)
        {
            if (into.Region.Length == 0) { into.Region = from.Region; into.RegionSource = from.RegionSource; }
            else
            {
                into.Region = "多个地区";
                if (into.RegionSource != from.RegionSource) into.RegionSource = "记录时的地区线索";
            }
        }
    }

    private readonly record struct TargetKey(string Process, string Protocol, string Address, int Port, string Hostname, string ProxyDestination, string ProxyNode, TrafficKind TrafficKind, string ProxyType);
    private readonly record struct SummaryStamp(long Upload, long Download, long Lost, DateTime Started, DateTime? Ended, DateTime UpdatedDay, string Status);
    private sealed class DayIndex
    {
        public long Upload, Download;
        public readonly Dictionary<TargetKey, TargetRow> Targets = new();
    }
    private sealed class SessionIndex
    {
        public readonly string Path;
        public SessionRecord? Record;
        public long MetadataLength = -1, SeenLength = -1, Offset, Upload, Download;
        public DateTime MetadataWriteUtc, FlowWriteUtc;
        public DateTime? FirstTimestampUtc;
        public int BadLines, ValidLines;
        public bool LastReadClosed, IncompleteClosedTail;
        public string MetadataError = "", FlowError = "";
        public readonly Dictionary<DateTime, DayIndex> Days = new();
        // UpdatedUtc changes with every heartbeat. Only its calendar day affects
        // dashboard accounting, so an idle recording need not rebuild every range.
        public (SummaryStamp Summary, long Length, long Offset, DateTime Written, bool Closed, bool Tail, string MetadataError, string FlowError, int BadLines) Stamp
            => (new(Record?.UploadBytes ?? 0, Record?.DownloadBytes ?? 0, Record?.EventsLost ?? 0,
                Record?.StartedUtc ?? default, Record?.EndedUtc, Record == null ? default : AsUtc(Record.UpdatedUtc).ToLocalTime().Date, Record?.Status ?? ""),
                SeenLength, Offset, FlowWriteUtc, LastReadClosed, IncompleteClosedTail, MetadataError, FlowError, BadLines);
        public SessionIndex(string path) => Path = path;
        public void ResetFlows()
        {
            SeenLength = -1; Offset = Upload = Download = 0; BadLines = ValidLines = 0;
            FirstTimestampUtc = null; LastReadClosed = IncompleteClosedTail = false; Days.Clear();
        }
    }
}
