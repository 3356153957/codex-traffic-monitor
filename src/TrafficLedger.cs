using System.Net;

namespace CodexTraffic;

// This class owns accounting only. ETW, the UI and the test harness share the same path.
public sealed class TrafficLedger
{
    private readonly Dictionary<string, TargetRow> _targets = new();
    private readonly Dictionary<string, FlowRecord> _pending = new();
    private readonly Dictionary<string, HashSet<string>> _reportedHosts = new();
    private readonly Dictionary<string, HashSet<string>> _reportedOutboundTypes = new();
    private readonly Dictionary<string, ProcessRow> _processes = new();
    public long UploadBytes { get; private set; }
    public long DownloadBytes { get; private set; }

    public void Add(DateTime at, int pid, string name, string path, string kind, string protocol,
        string localAddress, int localPort, string remoteAddress, int remotePort, long bytes, bool upload)
    {
        if (bytes <= 0) return;
        string route = IPAddress.TryParse(remoteAddress, out var ip) ? AddressClassifier.Classify(ip) : "未知地址";
        string targetKey = TargetKey(pid, name, protocol, localAddress, localPort, remoteAddress, remotePort);
        if (!_targets.TryGetValue(targetKey, out var target))
        {
            target = new TargetRow { Pid = pid, Process = name, Protocol = protocol, LocalAddress = localAddress, LocalPort = localPort, Address = remoteAddress, Port = remotePort, Target = AddressClassifier.Endpoint(remoteAddress, remotePort), Route = route };
            _targets.Add(targetKey, target);
        }
        target.LastSeen = at.ToLocalTime();
        if (upload) { target.UploadBytes += bytes; UploadBytes += bytes; }
        else { target.DownloadBytes += bytes; DownloadBytes += bytes; }

        string processKey = $"{pid}|{name}|{path}|{kind}";
        if (!_processes.TryGetValue(processKey, out var process))
            _processes[processKey] = process = new ProcessRow { Pid = pid, Name = name, Kind = kind };
        if (upload) process.UploadBytes += bytes; else process.DownloadBytes += bytes;

        string flowKey = $"{targetKey}|{localAddress}|{localPort}|{path}|{at.ToLocalTime():yyyy-MM-dd}";
        if (!_pending.TryGetValue(flowKey, out var flow))
        {
            flow = new FlowRecord { TimestampUtc = at.ToUniversalTime(), Pid = pid, Process = name, ProcessPath = path, Protocol = protocol, LocalAddress = localAddress, LocalPort = localPort, RemoteAddress = remoteAddress, RemotePort = remotePort, Route = route, ProxyType = "Unknown" };
            _pending.Add(flowKey, flow);
        }
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

    private static string TargetKey(int pid, string name, string protocol, string localAddress, int localPort, string remoteAddress, int remotePort)
        => $"{pid}|{name}|{protocol}|{remoteAddress}|{remotePort}|{localAddress}|{localPort}";

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
        string key = TargetKey(flow.Pid, flow.Process, flow.Protocol, flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, flow.RemotePort);
        if (!_targets.TryGetValue(key, out var target)) return;
        if (!_reportedHosts.TryGetValue(key, out var hosts)) _reportedHosts[key] = hosts = new();
        if (!string.IsNullOrEmpty(hint.Host)) hosts.Add(hint.Host);
        target.Hostname = string.Join(" / ", hosts.Take(3)) + (hosts.Count > 3 ? " 等" : "");
        target.DomainSource = "代理报告";
        target.ProxyDestination = hosts.Count <= 1 ? AddressClassifier.Endpoint(string.IsNullOrEmpty(hint.DestinationAddress) ? hint.Host : hint.DestinationAddress, hint.DestinationPort) : "多个目标，详见会话明细";
        target.ProxyChain = hint.Chain;
        target.ProxyNode = hint.Node;
        RecordTargetType(key, target, hint.OutboundType);
        target.Region = hint.Region;
        target.RegionSource = flow.RegionSource;
    }

    public void MarkUnconfirmed(FlowRecord flow)
    {
        flow.ProxyType = "Unknown";
        string key = TargetKey(flow.Pid, flow.Process, flow.Protocol, flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, flow.RemotePort);
        if (_targets.TryGetValue(key, out var target)) RecordTargetType(key, target, flow.ProxyType);
    }

    private void RecordTargetType(string key, TargetRow target, string type)
    {
        if (!_reportedOutboundTypes.TryGetValue(key, out var types)) _reportedOutboundTypes[key] = types = new(StringComparer.OrdinalIgnoreCase);
        types.Add(type);
        // The live socket row spans all its intervals; mixed policies must not be
        // presented as if its entire lifetime had the latest interval's policy.
        target.ProxyType = types.Count == 1 ? type : "Mixed";
        TrafficClassifier.Apply(target);
    }

    public List<TargetRow> Targets() => _targets.Values.OrderByDescending(x => x.UploadBytes + x.DownloadBytes).Select(x => new TargetRow
    {
        Target = x.Target, Address = x.Address, LocalAddress = x.LocalAddress, LocalPort = x.LocalPort, Port = x.Port, Pid = x.Pid, Process = x.Process, Protocol = x.Protocol,
        Route = x.Route, Hostname = x.Hostname, DomainSource = x.DomainSource, ProxyDestination = x.ProxyDestination, ProxyChain = x.ProxyChain, ProxyNode = x.ProxyNode, ProxyType = x.ProxyType, TrafficKind = x.TrafficKind, TrafficKindSource = x.TrafficKindSource, Region = x.Region, RegionSource = x.RegionSource, UploadBytes = x.UploadBytes, DownloadBytes = x.DownloadBytes, LastSeen = x.LastSeen
    }).ToList();

    public List<ProcessRow> Processes() => _processes.Values.Select(x => new ProcessRow { Name = x.Name, Pid = x.Pid, Kind = x.Kind, UploadBytes = x.UploadBytes, DownloadBytes = x.DownloadBytes }).ToList();
}
