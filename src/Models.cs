using System.Net;
using System.Net.Sockets;

namespace CodexTraffic;

public enum DashboardRange { All, Today, Last7Days, Last30Days }
public enum DashboardTrafficScope { All, Proxy }
public enum TrafficKind { Unknown, Direct, Proxy }

public sealed class DailyUsageRow
{
    public DateTime Date { get; set; }
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
}

public sealed class DashboardSnapshot
{
    public DashboardRange Range { get; set; }
    public DashboardTrafficScope TrafficScope { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public DateTime? FirstRecordedUtc { get; set; }
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public long AllUploadBytes { get; set; }
    public long AllDownloadBytes { get; set; }
    public long ProxyUploadBytes { get; set; }
    public long ProxyDownloadBytes { get; set; }
    public long DirectUploadBytes { get; set; }
    public long DirectDownloadBytes { get; set; }
    public long UnknownUploadBytes { get; set; }
    public long UnknownDownloadBytes { get; set; }
    public long TodayUploadBytes { get; set; }
    public long TodayDownloadBytes { get; set; }
    public int RecordedDays { get; set; }
    public int SessionCount { get; set; }
    public bool HasRecords { get; set; }
    public bool IsLoading { get; set; }
    public string Warning { get; set; } = "";
    public List<DailyUsageRow> DailyUsage { get; set; } = new();
    public List<TargetRow> Targets { get; set; } = new();
}

public sealed class MonitorSnapshot
{
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public double UploadBytesPerSecond { get; set; }
    public double DownloadBytesPerSecond { get; set; }
    public int ProcessCount { get; set; }
    public int ConnectionCount { get; set; }
    public long EventsLost { get; set; }
    public bool HasCapturedData { get; set; }
    public string CaptureStatus { get; set; } = "连接查看";
    public string Warning { get; set; } = "";
    public string LogFile { get; set; } = "";
    public List<TargetRow> Targets { get; set; } = new();
    public List<ProcessRow> Processes { get; set; } = new();
    public List<SessionRow> Sessions { get; set; } = new();
}

public sealed class TargetRow
{
    public string Target { get; set; } = "";
    public string Address { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public int Port { get; set; }
    public int Pid { get; set; }
    public string Process { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Route { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string DomainSource { get; set; } = "";
    public string ProxyDestination { get; set; } = "";
    public string ProxyChain { get; set; } = "";
    public string ProxyNode { get; set; } = "";
    public string ProxyType { get; set; } = "";
    public TrafficKind TrafficKind { get; set; }
    public string TrafficKindSource { get; set; } = "";
    public string Region { get; set; } = "";
    public string RegionSource { get; set; } = "";
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class ProcessRow
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public string Kind { get; set; } = "core";
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
}

public sealed class SessionRow
{
    public string StartedAt { get; set; } = "";
    public string Duration { get; set; } = "";
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public string Path { get; set; } = "";
}

public sealed class FlowRecord
{
    public DateTime TimestampUtc { get; set; }
    public int Pid { get; set; }
    public string Process { get; set; } = "";
    public string ProcessPath { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; }
    public string Route { get; set; } = "";
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public string ProxyReportedHost { get; set; } = "";
    public string ProxyReportedAddress { get; set; } = "";
    public int ProxyReportedPort { get; set; }
    public string ProxyReportedChain { get; set; } = "";
    public string ProxyNode { get; set; } = "";
    public string ProxyType { get; set; } = "";
    public string Region { get; set; } = "";
    public string RegionSource { get; set; } = "";
}

public sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public long EventsLost { get; set; }
    public bool IncludeChildren { get; set; }
    public bool IncludeLoopback { get; set; }
    public string Status { get; set; } = "recording";
    public string Error { get; set; } = "";
    public string ByteBasis { get; set; } = "Windows ETW TCP/UDP Send/Receive transfer bytes; excludes separately reported retransmit events";
}

public static class AddressClassifier
{
    public static string Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return "本机回环（可能代理）";
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return "虚拟地址（可能 TUN）";
            if (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "局域网 / 私有地址";
            if (b[0] == 0 || b[0] >= 224 || b[0] == 192 && b[1] == 0 && b[2] == 2 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113) return "保留 / 组播地址";
            return "公网地址";
        }
        if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return "保留 / 组播地址";
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (b[0] & 0xfe) == 0xfc) return "局域网 / 私有地址";
        return "公网地址";
    }

    public static string Endpoint(string address, int port) => address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";
}

public readonly record struct EndpointPair(string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort);

public static class EndpointResolver
{
    public static EndpointPair Resolve(string protocol, bool upload, string source, int sourcePort, string destination, int destinationPort)
    {
        // Kernel TCP events v1/v2 keep saddr/sport local for BOTH Send and Recv.
        // UDP events observed on Windows use packet direction. Do not apply the TCP
        // convention to UDP; loopback makes address-only guesses ambiguous.
        bool sourceIsLocal = protocol.StartsWith("TCP", StringComparison.Ordinal) || upload;
        return sourceIsLocal ? new(source, sourcePort, destination, destinationPort) : new(destination, destinationPort, source, sourcePort);
    }
}
