using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace CodexTraffic;

public sealed class MonitorEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly ProcessCatalog _catalog = new();
    private readonly HistoryAnalytics _analytics;
    private readonly GeoIpHints _geo = new();
    private readonly DnsCacheHints _dns = new();
    private readonly LocalProxyHints _proxy = new();
    private readonly System.Threading.Timer _timer;
    private TrafficLedger _ledger = new();
    private TraceEventSession? _session;
    private SessionStore? _store;
    private Task? _captureTask;
    private bool _desired, _disposed, _running;
    private string _error = "";
    private DateTime? _startedAt;
    private string _logFile = "";
    private long _lost;
    private long _lastUp, _lastDown;
    private long _lastTicks = Stopwatch.GetTimestamp();
    private double _upRate, _downRate;
    private int _maintenanceActive;
    private int _disposeStarted;
    private List<ConnectionInfo> _connections = new();
    private List<SessionRow> _history = new();
    private Dictionary<string, ConnectionInfo> _connectionMap = new();

    public bool IncludeChildren { get; set; } = true;
    public bool IncludeLoopback { get; set; } = true;
    public string DataDirectory { get; }
    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public bool IsRunning { get { lock (_gate) return _desired; } }
    public string LastError { get { lock (_gate) return _error; } }

    public MonitorEngine(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTrafficMonitor");
        Directory.CreateDirectory(DataDirectory);
        _analytics = new HistoryAnalytics(DataDirectory);
        _history = SessionStore.History(DataDirectory);
        _timer = new System.Threading.Timer(_ => Maintain(), null, 0, 1000);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _desired) return;
            if (!IsAdmin) { _error = "Windows 内核流量统计需要管理员权限，请点击管理员启动。"; return; }
            if (_captureTask is { IsCompleted: false }) { _error = "上一轮采集正在收尾，请稍后再开始。"; return; }
            _error = "";
            _desired = true;
            _captureTask = Task.Run(Capture);
        }
    }

    private void Capture()
    {
        TraceEventSession? session = null;
        try
        {
            _catalog.Refresh();
            session = new TraceEventSession("CodexTraffic-" + Environment.ProcessId) { StopOnDispose = true, BufferSizeMB = 64 };
            lock (_gate)
            {
                if (!_desired) return;
                _session = session;
                _store = new SessionStore(DataDirectory, IncludeChildren, IncludeLoopback);
                _logFile = _store.LogFile;
                _ledger = new TrafficLedger();
                _startedAt = DateTime.Now;
                _lastUp = _lastDown = _lost = 0;
                _lastTicks = Stopwatch.GetTimestamp();
                _upRate = _downRate = 0;
            }
            // Source starts a user-provider session if accessed first. Kernel must be
            // enabled before touching Source, or TraceEvent rejects this session.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
            var parser = session.Source.Kernel;
            parser.ProcessStart += d => _catalog.RecordStarted(d.ProcessID, d.ParentID, d.ImageFileName, d.TimeStamp);
            parser.ProcessStop += d => _catalog.RecordStopped(d.ProcessID, d.TimeStamp);
            parser.TcpIpSend += d => Transfer(d.TimeStamp, d.ProcessID, "TCP", true, d.saddr, d.sport, d.daddr, d.dport, d.size);
            parser.TcpIpRecv += d => Transfer(d.TimeStamp, d.ProcessID, "TCP", false, d.saddr, d.sport, d.daddr, d.dport, d.size);
            parser.TcpIpSendIPV6 += d => Transfer(d.TimeStamp, d.ProcessID, "TCP6", true, d.saddr, d.sport, d.daddr, d.dport, d.size);
            parser.TcpIpRecvIPV6 += d => Transfer(d.TimeStamp, d.ProcessID, "TCP6", false, d.saddr, d.sport, d.daddr, d.dport, d.size);
            parser.UdpIpSend += d => Transfer(d.TimeStamp, d.ProcessID, "UDP", true, d.saddr, d.sport, d.daddr, d.dport, d.size);
            parser.UdpIpRecv += d => Transfer(d.TimeStamp, d.ProcessID, "UDP", false, d.saddr, d.sport, d.daddr, d.dport, d.size);
            parser.UdpIpSendIPV6 += d => Transfer(d.TimeStamp, d.ProcessID, "UDP6", true, d.saddr, d.sport, d.daddr, d.dport, d.size);
            parser.UdpIpRecvIPV6 += d => Transfer(d.TimeStamp, d.ProcessID, "UDP6", false, d.saddr, d.sport, d.daddr, d.dport, d.size);
            lock (_gate) { if (!_desired) return; _running = true; }
            session.Source.Process();
            lock (_gate) if (_desired && _error.Length == 0) _error = "ETW 会话意外结束，请重新开始采集。";
        }
        catch (Exception e)
        {
            lock (_gate) if (_desired) _error = "无法采集：" + e.Message;
        }
        finally
        {
            // Disposing only our uniquely named session never stops another profiler.
            try { session?.Dispose(); } catch { }
            lock (_gate)
            {
                _running = _desired = false;
                _session = null;
                _upRate = _downRate = 0;
                try
                {
                    Flush();
                    _store?.Update(_ledger.UploadBytes, _ledger.DownloadBytes, _lost, _error.Length > 0 ? _error : null, true);
                }
                catch (Exception e) { _error = "保存记录失败：" + e.Message; }
                finally
                {
                    if (_error.Length > 0)
                        try { _store?.Update(_ledger.UploadBytes, _ledger.DownloadBytes, _lost, _error, true); } catch { }
                    try { _store?.Dispose(); } catch (Exception e) { _error = "关闭记录失败：" + e.Message; }
                }
                _store = null;
                _history = SessionStore.History(DataDirectory);
            }
        }
    }

    private void Transfer(DateTime at, int pid, string protocol, bool upload, IPAddress source, int sourcePort, IPAddress dest, int destPort, long bytes)
    {
        if (bytes <= 0) return;
        var process = _catalog.GetTracked(pid, IncludeChildren, at);
        if (process == null) return;
        string a = source.ToString(), b = dest.ToString();
        string local, remote;
        int localPort, remotePort;
        lock (_gate)
        {
            if (!_running) return;
            if (protocol.StartsWith("TCP", StringComparison.Ordinal) && _connectionMap.TryGetValue(ConnectionKey(pid, a, sourcePort, b, destPort), out var known))
            {
                local = known.LocalAddress; localPort = known.LocalPort;
                remote = known.RemoteAddress; remotePort = known.RemotePort;
            }
            else
            {
                var endpoints = EndpointResolver.Resolve(protocol, upload, a, sourcePort, b, destPort);
                local = endpoints.LocalAddress; localPort = endpoints.LocalPort;
                remote = endpoints.RemoteAddress; remotePort = endpoints.RemotePort;
            }
            if (!IncludeLoopback && IPAddress.TryParse(remote, out var ip) && IPAddress.IsLoopback(ip)) return;
            _ledger.Add(at, pid, process.Name, process.Path, process.Kind, protocol, local, localPort, remote, remotePort, bytes, upload);
        }
    }

    private static string ConnectionKey(int pid, string a, int ap, string b, int bp) => $"{pid}|{a}|{ap}|{b}|{bp}";

    private void Maintain()
    {
        if (_disposed || Interlocked.Exchange(ref _maintenanceActive, 1) != 0) return;
        try
        {
            _catalog.Refresh();
            var connections = ConnectionReader.GetTcpConnections().ToList();
            var map = new Dictionary<string, ConnectionInfo>();
            foreach (var c in connections)
            {
                map[ConnectionKey(c.Pid, c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort)] = c;
            }
            lock (_gate)
            {
                _connections = connections;
                _connectionMap = map;
                long now = Stopwatch.GetTimestamp();
                double elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
                _upRate = _running && elapsed > 0 ? (_ledger.UploadBytes - _lastUp) / elapsed : 0;
                _downRate = _running && elapsed > 0 ? (_ledger.DownloadBytes - _lastDown) / elapsed : 0;
                _lastUp = _ledger.UploadBytes; _lastDown = _ledger.DownloadBytes; _lastTicks = now;
                if (_running)
                {
                    _lost = _session?.EventsLost ?? _lost;
                    try { Flush(); }
                    catch (Exception e)
                    {
                        _error = "记录写入失败，已停止采集：" + e.Message;
                        _desired = false;
                        ThreadPool.QueueUserWorkItem(_ => Stop());
                    }
                }
            }
            _analytics.Refresh();
        }
        catch (Exception e)
        {
            lock (_gate) _error = "刷新连接失败：" + e.Message;
        }
        finally { Interlocked.Exchange(ref _maintenanceActive, 0); }
    }

    private void Flush()
    {
        if (_store == null) return;
        var pending = _ledger.Pending();
        foreach (var row in pending)
        {
            var hint = _proxy.Lookup(row.Pid, row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort, row.TimestampUtc);
            if (hint != null) _ledger.Annotate(row, hint);
            else _ledger.MarkUnconfirmed(row);
            if (row.Region.Length == 0)
            {
                var geo = _geo.Lookup(hint?.DestinationAddress ?? row.RemoteAddress);
                row.Region = geo.Region ?? ""; row.RegionSource = geo.Source ?? "";
            }
        }
        _store.Append(pending);
        _ledger.CommitPending();
        _store.Update(_ledger.UploadBytes, _ledger.DownloadBytes, _lost);
    }

    public MonitorSnapshot GetSnapshot(bool includeTargets = true)
    {
        var tracked = _catalog.List(IncludeChildren);
        lock (_gate)
        {
            var targets = includeTargets ? _ledger.Targets() : new List<TargetRow>();
            if (includeTargets && _startedAt == null)
                targets = _connections.Where(c => _catalog.IsTracked(c.Pid, IncludeChildren) && c.RemotePort != 0 && (IncludeLoopback || !IPAddress.IsLoopback(IPAddress.Parse(c.RemoteAddress))))
                    .Select(c => new TargetRow { Address = c.RemoteAddress, Port = c.RemotePort, LocalAddress = c.LocalAddress, LocalPort = c.LocalPort, Target = AddressClassifier.Endpoint(c.RemoteAddress, c.RemotePort), Process = _catalog.Get(c.Pid)?.Name ?? "未知", Pid = c.Pid, Protocol = c.Protocol, Route = AddressClassifier.Classify(IPAddress.Parse(c.RemoteAddress)) }).ToList();
            var processes = _ledger.Processes();
            foreach (var target in targets)
            {
                if (!string.IsNullOrEmpty(target.Hostname)) continue;
                var hint = _proxy.Lookup(target.Pid, target.LocalAddress, target.LocalPort, target.Address, target.Port);
                if (hint != null)
                {
                    target.Hostname = !string.IsNullOrEmpty(hint.Host) ? hint.Host : _dns.Lookup(target.Address);
                    target.DomainSource = !string.IsNullOrEmpty(hint.Host) ? "代理报告" : "DNS 缓存";
                    target.ProxyDestination = AddressClassifier.Endpoint(string.IsNullOrEmpty(hint.DestinationAddress) ? hint.Host : hint.DestinationAddress, hint.DestinationPort);
                    target.ProxyChain = hint.Chain;
                    target.ProxyNode = hint.Node;
                    target.ProxyType = hint.OutboundType;
                    target.Region = hint.Region;
                    target.RegionSource = hint.Region.Length > 0 ? "代理报告的目标 GeoIP" : "";
                }
                else { target.Hostname = _dns.Lookup(target.Address); target.DomainSource = "DNS 缓存"; }
            }
            foreach (var target in targets) { _geo.Enrich(target); TrafficClassifier.Apply(target); }
            foreach (var p in tracked)
                if (!processes.Any(x => x.Pid == p.Pid)) processes.Add(new ProcessRow { Name = p.Name, Pid = p.Pid, Kind = p.Kind });
            var sessions = _history.ToList();
            if (_store != null)
                sessions.Insert(0, new SessionRow { StartedAt = _store.Record.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Duration = (DateTime.UtcNow - _store.Record.StartedUtc).ToString(@"hh\:mm\:ss") + "（本次）", UploadBytes = _ledger.UploadBytes, DownloadBytes = _ledger.DownloadBytes, Path = _store.DirectoryPath });
            return new MonitorSnapshot
            {
                CapturedAt = DateTime.Now, StartedAt = _startedAt, UploadBytes = _ledger.UploadBytes, DownloadBytes = _ledger.DownloadBytes,
                UploadBytesPerSecond = _upRate, DownloadBytesPerSecond = _downRate, ProcessCount = tracked.Count,
                ConnectionCount = _connections.Count(c => _catalog.IsTracked(c.Pid, IncludeChildren) && c.RemotePort != 0), EventsLost = _lost,
                HasCapturedData = _startedAt.HasValue, Targets = targets, Processes = processes.OrderByDescending(x => x.UploadBytes + x.DownloadBytes).ToList(), Sessions = sessions,
                CaptureStatus = _running ? "正在记录" : _desired ? "正在启动" : _startedAt.HasValue ? "已停止记录" : "连接查看 · 未计量",
                Warning = _lost > 0 ? $"ETW 丢失 {_lost:N0} 个事件，统计可能偏低。" : "记录 Codex 进程的直接网络连接；回环可能是本地代理，虚拟地址无法还原最终网站。", LogFile = _logFile
            };
        }
    }

    public void ExportCsv(string path)
    {
        lock (_gate)
        {
            if (_startedAt == null) throw new InvalidOperationException("尚未开始流量采集，当前只有连接信息。请先以管理员身份开始记录。");
            SessionStore.WriteCsv(path, GetSnapshot().Targets);
        }
    }

    public DashboardSnapshot GetDashboardSnapshot(DashboardRange range, DashboardTrafficScope scope = DashboardTrafficScope.All)
    {
        var snapshot = _analytics.GetSnapshot(range, scope);
        foreach (var target in snapshot.Targets) _geo.Enrich(target);
        return snapshot;
    }

    public void ExportDashboardCsv(string path, DashboardRange range, DashboardTrafficScope scope = DashboardTrafficScope.All)
    {
        var snapshot = GetDashboardSnapshot(range, scope);
        if (snapshot.IsLoading) throw new InvalidOperationException("历史记录正在载入，请稍后导出。");
        if (!snapshot.HasRecords) throw new InvalidOperationException("当前范围内还没有流量记录。");
        SessionStore.WriteCsv(path, snapshot.Targets);
    }

    public void Stop()
    {
        TraceEventSession? session;
        lock (_gate) { _desired = false; session = _session; }
        try { session?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        _timer.Dispose();
        Stop();
        try { _captureTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _dns.Dispose();
        _proxy.Dispose();
        _geo.Dispose();
    }
}
