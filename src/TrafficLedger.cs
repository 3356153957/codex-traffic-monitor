using System.Net;

namespace CodexTraffic;

// This class owns accounting only. ETW, the UI and the test harness share the same path.
public sealed class TrafficLedger
{
    // Packet callbacks are the hot path. Value keys retain the complete socket
    // identity without formatting several long strings for every transfer.
    private readonly record struct TargetIdentity(TrackedApplication Application, int Pid, string Name,
        string Protocol, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort);
    private readonly record struct ProcessIdentity(TrackedApplication Application, int Pid, string Name, string Path, string Kind);
    private readonly record struct FlowIdentity(TargetIdentity Target, string ProcessPath, DateOnly LocalDate);
    // The socket summary displays only three distinct names and whether more
    // exist. Detailed annotations remain intact in each persisted FlowRecord.
    // Keeping every name and a separate HashSet for outbound types otherwise
    // makes a long-lived proxy socket retain an unlimited amount of history.
    private sealed class TargetState
    {
        public required TargetRow Row { get; init; }
        private string? _host1, _host2, _host3;
        private bool _moreHosts;
        private string? _outboundType;
        private bool _mixedOutboundTypes;

        public bool HasMultipleHosts => _host2 != null;

        public void RecordHost(string host)
        {
            if (host.Length == 0 || _moreHosts || host == _host1 || host == _host2 || host == _host3) return;
            if (_host1 == null) _host1 = host;
            else if (_host2 == null) _host2 = host;
            else if (_host3 == null) _host3 = host;
            else _moreHosts = true;
            Row.Hostname = _host1 + (_host2 == null ? "" : " / " + _host2) +
                (_host3 == null ? "" : " / " + _host3) + (_moreHosts ? " 等" : "");
        }

        public void RecordOutboundType(string type)
        {
            if (_outboundType != null && !StringComparer.OrdinalIgnoreCase.Equals(_outboundType, type))
                _mixedOutboundTypes = true;
            _outboundType = type;
            // The row spans all intervals, so a later policy cannot overwrite
            // the fact that this socket has used different outbound types.
            Row.ProxyType = _mixedOutboundTypes ? "Mixed" : type;
            TrafficClassifier.Apply(Row);
        }
    }

    private readonly Dictionary<TargetIdentity, TargetState> _targets = new();
    private readonly Dictionary<FlowIdentity, FlowRecord> _pending = new();
    private readonly Dictionary<ProcessIdentity, ProcessRow> _processes = new();
    // Repeated loopback addresses/endpoints dominate proxy-heavy sessions.
    // This is a bounded session-local pool, never the process-wide intern table.
    private readonly Dictionary<string, string> _endpointStrings = new(StringComparer.Ordinal);
    private DateTime _lastTransferUtc;
    public long UploadBytes { get; private set; }
    public long DownloadBytes { get; private set; }
    public long CodexUploadBytes { get; private set; }
    public long CodexDownloadBytes { get; private set; }
    public long ClaudeUploadBytes { get; private set; }
    public long ClaudeDownloadBytes { get; private set; }
    public bool HasPending => _pending.Count > 0;

    public void Add(DateTime at, int pid, string name, string path, string kind, string protocol,
        string localAddress, int localPort, string remoteAddress, int remotePort, long bytes, bool upload, TrackedApplication application = TrackedApplication.Codex)
    {
        if (bytes <= 0) return;
        var targetKey = TargetKey(pid, name, protocol, localAddress, localPort, remoteAddress, remotePort, application);
        if (!_targets.TryGetValue(targetKey, out var state))
        {
            localAddress = ShareEndpointString(localAddress);
            remoteAddress = ShareEndpointString(remoteAddress);
            targetKey = TargetKey(pid, name, protocol, localAddress, localPort, remoteAddress, remotePort, application);
            string route = IPAddress.TryParse(remoteAddress, out var ip) ? AddressClassifier.Classify(ip) : "未知地址";
            state = new TargetState { Row = new TargetRow { Application = application, Pid = pid, Process = name, Protocol = protocol, LocalAddress = localAddress, LocalPort = localPort, Address = remoteAddress, Port = remotePort, Target = ShareEndpointString(AddressClassifier.Endpoint(remoteAddress, remotePort)), Route = route } };
            _targets.Add(targetKey, state);
        }
        var target = state.Row;
        var localTime = at.ToLocalTime();
        var timestampUtc = at.ToUniversalTime();
        if (target.LastSeen == default || target.LastSeen.ToUniversalTime() < timestampUtc) target.LastSeen = localTime;
        if (_lastTransferUtc < timestampUtc) _lastTransferUtc = timestampUtc;
        if (upload) { target.UploadBytes += bytes; UploadBytes += bytes; }
        else { target.DownloadBytes += bytes; DownloadBytes += bytes; }
        if (application == TrackedApplication.Claude)
        { if (upload) ClaudeUploadBytes += bytes; else ClaudeDownloadBytes += bytes; }
        else
        { if (upload) CodexUploadBytes += bytes; else CodexDownloadBytes += bytes; }

        var processKey = new ProcessIdentity(application, pid, name, path, kind);
        if (!_processes.TryGetValue(processKey, out var process))
            _processes[processKey] = process = new ProcessRow { Application = application, Pid = pid, Name = name, Kind = kind };
        if (upload) process.UploadBytes += bytes; else process.DownloadBytes += bytes;

        var flowKey = new FlowIdentity(targetKey, path, DateOnly.FromDateTime(localTime));
        if (!_pending.TryGetValue(flowKey, out var flow))
        {
            flow = new FlowRecord { Application = application, TimestampUtc = timestampUtc, LastTimestampUtc = timestampUtc, Pid = pid, Process = name, ProcessPath = path, Protocol = protocol, LocalAddress = localAddress, LocalPort = localPort, RemoteAddress = remoteAddress, RemotePort = remotePort, Route = target.Route, ProxyType = "Unknown" };
            _pending.Add(flowKey, flow);
        }
        // ETW events from different CPUs may arrive out of timestamp order. A
        // batch needs both bounds before a controller hint can cover its bytes.
        if (timestampUtc < flow.TimestampUtc) flow.TimestampUtc = timestampUtc;
        if (timestampUtc > flow.LastTimestampUtc) flow.LastTimestampUtc = timestampUtc;
        if (upload) flow.UploadBytes += bytes; else flow.DownloadBytes += bytes;
    }

    public List<FlowRecord> Drain()
    {
        var rows = _pending.Values.ToList();
        _pending.Clear();
        return rows;
    }

    public List<FlowRecord> Pending() => _pending.Values.ToList();
    public void CommitPending() => _pending.Clear();

    // Empty dictionary buckets still retain the largest recent interval batch.
    // Release them only after an idle period to avoid reallocation while busy.
    public void TrimIdleBuffers(DateTime utcNow)
    {
        if (_pending.Count == 0 && _lastTransferUtc != default && utcNow.ToUniversalTime() - _lastTransferUtc >= TimeSpan.FromSeconds(30))
            _pending.TrimExcess();
    }

    private static TargetIdentity TargetKey(int pid, string name, string protocol, string localAddress, int localPort, string remoteAddress, int remotePort, TrackedApplication application)
        => new(application, pid, name, protocol, localAddress, localPort, remoteAddress, remotePort);

    private string ShareEndpointString(string value)
    {
        if (_endpointStrings.TryGetValue(value, out var shared)) return shared;
        if (_endpointStrings.Count < 1024) _endpointStrings.Add(value, value);
        return value;
    }

    public void Annotate(FlowRecord flow, ProxyHint hint)
    {
        flow.ProxyReportedHost = hint.Host;
        flow.ProxyReportedAddress = hint.DestinationAddress;
        flow.ProxyReportedPort = hint.DestinationPort;
        flow.ProxyReportedChain = hint.Chain;
        flow.ProxyNode = hint.Node;
        flow.ProxyType = hint.OutboundType;
        flow.Region = hint.Region;
        flow.RegionSource = hint.Region.Length > 0 ? "代理报告的目标 GeoIP" : "";
        var key = TargetKey(flow.Pid, flow.Process, flow.Protocol, flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, flow.RemotePort, flow.Application);
        if (!_targets.TryGetValue(key, out var state)) return;
        var target = state.Row;
        state.RecordHost(hint.Host);
        target.DomainSource = "代理报告";
        target.ProxyDestination = !state.HasMultipleHosts ? AddressClassifier.Endpoint(string.IsNullOrEmpty(hint.DestinationAddress) ? hint.Host : hint.DestinationAddress, hint.DestinationPort) : "多个目标，详见会话明细";
        target.ProxyChain = hint.Chain;
        target.ProxyNode = hint.Node;
        state.RecordOutboundType(hint.OutboundType);
        target.Region = hint.Region;
        target.RegionSource = flow.RegionSource;
    }

    public void MarkUnconfirmed(FlowRecord flow)
    {
        flow.ProxyType = "Unknown";
        var key = TargetKey(flow.Pid, flow.Process, flow.Protocol, flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, flow.RemotePort, flow.Application);
        if (_targets.TryGetValue(key, out var state)) state.RecordOutboundType(flow.ProxyType);
    }

    public List<TargetRow> Targets(DashboardApplicationScope scope = DashboardApplicationScope.Total)
        => CopyTargets(scope).OrderByDescending(x => x.UploadBytes + x.DownloadBytes).ToList();

    // Caller holds its accounting gate only for the copy; sorting detached rows
    // can then happen without delaying packet ingestion.
    internal List<TargetRow> CopyTargets(DashboardApplicationScope scope) => _targets.Values.Select(x => x.Row).Where(x => ApplicationFilter.Matches(x.Application, scope)).Select(x => new TargetRow
    {
        Application = x.Application, Target = x.Target, Address = x.Address, LocalAddress = x.LocalAddress, LocalPort = x.LocalPort, Port = x.Port, Pid = x.Pid, Process = x.Process, Protocol = x.Protocol,
        Route = x.Route, Hostname = x.Hostname, DomainSource = x.DomainSource, ProxyDestination = x.ProxyDestination, ProxyChain = x.ProxyChain, ProxyNode = x.ProxyNode, ProxyType = x.ProxyType, TrafficKind = x.TrafficKind, TrafficKindSource = x.TrafficKindSource, Region = x.Region, RegionSource = x.RegionSource, UploadBytes = x.UploadBytes, DownloadBytes = x.DownloadBytes, LastSeen = x.LastSeen
    }).ToList();

    public List<ProcessRow> Processes(DashboardApplicationScope scope = DashboardApplicationScope.Total) => _processes.Values.Where(x => ApplicationFilter.Matches(x.Application, scope)).Select(x => new ProcessRow { Application = x.Application, Name = x.Name, Pid = x.Pid, Kind = x.Kind, UploadBytes = x.UploadBytes, DownloadBytes = x.DownloadBytes }).ToList();

    public (long Upload, long Download) Totals(DashboardApplicationScope scope) => scope switch
    {
        DashboardApplicationScope.Codex => (CodexUploadBytes, CodexDownloadBytes),
        DashboardApplicationScope.Claude => (ClaudeUploadBytes, ClaudeDownloadBytes),
        _ => (UploadBytes, DownloadBytes)
    };
}
