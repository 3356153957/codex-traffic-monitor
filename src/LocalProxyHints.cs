using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace CodexTraffic;

public sealed record ProxyHint(string Host, string DestinationAddress, int DestinationPort,
    string Chain, string Source, DateTime ObservedUtc, string Node = "", string Region = "", string OutboundType = "");

/// <summary>
/// Optional, read-only hints from a local Mihomo controller. These are proxy reports,
/// never byte counters or evidence that the reported destination was reached.
/// Only local controllers are consulted; subscription files are not opened.
/// </summary>
public sealed class LocalProxyHints : IDisposable
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly System.Threading.Timer _timer;
    private readonly ProxyHintCache _cache = new();
    private readonly ProxyTypeCache _types = new();
    private readonly ProxyControllerDiscoveryCache _controllers;
    private readonly ProxyControllerPollOrder _pollOrder = new();
    private readonly ProxyHttpClientPool _clients = new(32);
    private readonly object _publishGate = new();
    private int _refreshing;
    private int _disposed;

    public LocalProxyHints() : this(ReadControllers, true) { }

    internal LocalProxyHints(Func<IEnumerable<LocalProxyController>> readControllers, bool startTimer)
    {
        _controllers = new ProxyControllerDiscoveryCache(readControllers);
        // The token remains usable by callbacks already in flight after Dispose.
        _stopToken = _stop.Token;
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null,
            startTimer ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// Matches a complete source endpoint. Event-time lookups may use connections
    /// seen within five seconds, bounded by controller start/absence observations.
    /// Ambiguous matches, stale snapshots and process conflicts yield no hint.
    /// A loopback proxy next hop or a fake-IP may differ from the reported target.
    /// </summary>
    public ProxyHint? Lookup(int pid, string localAddress, int localPort, string remoteAddress, int remotePort,
        DateTime? observedAtUtc = null, DateTime? observedUntilUtc = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        return _cache.Lookup(pid, localAddress, localPort, remoteAddress, remotePort,
            DateTime.UtcNow, observedAtUtc, MatchesProcess, observedUntilUtc);
    }

    internal async Task RefreshAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            foreach (var controller in _pollOrder.Order(_controllers.Get(DateTime.UtcNow)))
            {
                try
                {
                    using var json = await ReadControllerJsonAsync(controller, "/connections").ConfigureAwait(false);
                    if (json == null) continue;
                    if (!json.RootElement.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array) continue;
                    DateTime observed = DateTime.UtcNow;
                    string controllerKey = ControllerKey(controller);
                    // Optional metadata must never delay the connections poll.
                    // This method catches all failures, and only strings survive
                    // disposal of the controller metadata response documents.
                    bool refreshTypes;
                    lock (_publishGate)
                        refreshTypes = Volatile.Read(ref _disposed) == 0 && _types.TryBeginRefresh(controllerKey, observed);
                    if (refreshTypes) _ = RefreshTypesAsync(controller, controllerKey);
                    var hints = new List<ProxyConnectionHint>();
                    foreach (var connection in connections.EnumerateArray())
                    {
                        if (!connection.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) continue;
                        string source = GetString(metadata, "sourceIP");
                        int sourcePort = GetInt(metadata, "sourcePort");
                        if (!TryAddress(source, out _) || sourcePort is < 1 or > 65535) continue;
                        string host = GetString(metadata, "host");
                        if (string.IsNullOrEmpty(host)) host = GetString(metadata, "sniffHost");
                        string address = GetString(metadata, "destinationIP");
                        int port = GetInt(metadata, "destinationPort");
                        if ((string.IsNullOrEmpty(host) && string.IsNullOrEmpty(address)) || port is < 1 or > 65535) continue;
                        string chain = "", node = "", region = "";
                        if (connection.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array)
                        {
                            var names = chains.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
                            chain = string.Join(" → ", names.Reverse());
                            node = names.FirstOrDefault() ?? "";
                        }
                        if (metadata.TryGetProperty("destinationGeoIP", out var geo) && geo.ValueKind == JsonValueKind.Array)
                            region = string.Join(" / ", geo.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => GeoIpHints.CountryName(x.GetString() ?? "")).Where(x => x.Length > 0));
                        int processId = GetInt(metadata, "processId");
                        if (processId <= 0) processId = GetInt(metadata, "pid");
                        string id = GetString(connection, "id");
                        DateTime? started = DateTimeOffset.TryParse(GetString(connection, "start"),
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var start) && start.UtcDateTime <= observed ? start.UtcDateTime : null;
                        string identity = id.Length is > 0 and <= 200
                            ? controllerKey + "|" + id : "";
                        hints.Add(new ProxyConnectionHint(identity, started, source, sourcePort, processId,
                            GetString(metadata, "process"), GetString(metadata, "processPath"),
                            new ProxyHint(host, address, port, chain, controller.PipeName.Length > 0
                                ? "代理报告 · 本机 Mihomo（命名管道）" : "代理报告 · 本机 Mihomo", observed, node, region,
                                _types.Lookup(controllerKey, node, observed))));
                    }
                    _pollOrder.Succeeded(controller);
                    PublishHints(hints, observed);
                    return;
                }
                catch { /* Missing, disabled or inaccessible controller is normal. No credentials or response bodies are logged. */ }
            }
            PublishHints(Array.Empty<ProxyConnectionHint>(), DateTime.UtcNow);
        }
        catch { PublishHints(Array.Empty<ProxyConnectionHint>(), DateTime.UtcNow); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    private void PublishHints(IEnumerable<ProxyConnectionHint> hints, DateTime observed)
    {
        lock (_publishGate)
            if (Volatile.Read(ref _disposed) == 0) _cache.Update(hints, observed);
    }

    private static string ControllerKey(LocalProxyController controller) => controller.PipeName + "|" + controller.Address.AbsoluteUri;

    private async Task RefreshTypesAsync(LocalProxyController controller, string controllerKey)
    {
        try
        {
            // Provider leaf nodes are not necessarily present in /proxies.
            // These are read-only local API snapshots, never subscription fetches.
            var globalTask = ReadOptionalControllerJsonAsync(controller, "/proxies");
            var providerTask = ReadOptionalControllerJsonAsync(controller, "/providers/proxies");
            await Task.WhenAll(globalTask, providerTask).ConfigureAwait(false);
            using var global = await globalTask.ConfigureAwait(false);
            using var providers = await providerTask.ConfigureAwait(false);
            var types = ProxyTypeCache.ParseTypes(global?.RootElement, providers?.RootElement);
            CompleteTypes(controllerKey, types);
        }
        catch
        {
            // A disabled/unsupported metadata endpoint leaves the type unknown.
            // It never discards or blocks the separately captured connections.
            CompleteTypes(controllerKey, null);
        }
    }

    private void CompleteTypes(string controllerKey, IReadOnlyDictionary<string, string>? types)
    {
        lock (_publishGate)
            if (Volatile.Read(ref _disposed) == 0) _types.CompleteRefresh(controllerKey, types, DateTime.UtcNow);
    }

    private async Task<JsonDocument?> ReadOptionalControllerJsonAsync(LocalProxyController controller, string path)
    {
        try { return await ReadControllerJsonAsync(controller, path).ConfigureAwait(false); }
        catch { return null; }
    }

    private async Task<JsonDocument?> ReadControllerJsonAsync(LocalProxyController controller, string path)
    {
        using var lease = _clients.TryAcquire(ControllerKey(controller), () => CreateClient(controller.PipeName));
        if (lease == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller.Address, path));
        if (!string.IsNullOrEmpty(controller.Secret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", controller.Secret);
        using var response = await lease.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes) return null;
        using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        // JsonDocument owns its parsing buffer. Bound the actual response bytes
        // while reading directly, avoiding an additional full MemoryStream copy.
        using var limited = new BoundedReadStream(body, MaximumResponseBytes);
        return await JsonDocument.ParseAsync(limited, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    private static IEnumerable<LocalProxyController> ReadControllers()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // All four optional files share the same local pipe inventory.
        string[] activePipes = ActiveMihomoPipes().ToArray();
        foreach (string directory in new[] { "io.github.clash-verge-rev.clash-verge-rev", "io.github.clash-verge-rev.clash-verge" })
        foreach (string name in new[] { "clash-verge.yaml", "config.yaml" })
        {
            var controllers = new List<LocalProxyController>();
            try
            {
                string file = Path.Combine(appData, directory, name);
                if (!File.Exists(file) || new FileInfo(file).Length > 4 * 1024 * 1024) continue;
                string endpoint = "", pipe = "", secret = "";
                int lineCount = 0;
                foreach (string line in File.ReadLines(file, Encoding.UTF8))
                {
                    if (++lineCount > 250) break;
                    if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') continue;
                    int colon = line.IndexOf(':');
                    if (colon < 1) continue;
                    string key = line[..colon].Trim();
                    // Stop before subscription/routing sections in the generated runtime file.
                    if (key is "proxies" or "proxy-providers" or "proxy-groups" or "rules" or "rule-providers" or "profiles") break;
                    if (key == "external-controller") endpoint = ReadYamlScalar(line[(colon + 1)..]);
                    else if (key == "external-controller-pipe") pipe = ReadYamlScalar(line[(colon + 1)..]);
                    else if (key == "secret") secret = ReadYamlScalar(line[(colon + 1)..]);
                }
                bool configuredPipe = TryLocalPipe(pipe, out string pipeName);
                if (configuredPipe && activePipes.Contains(pipeName, StringComparer.OrdinalIgnoreCase))
                    controllers.Add(new LocalProxyController(new Uri("http://127.0.0.1/"), secret, pipeName));
                // Verge can leave a release pipe in config after switching to an alpha
                // core. Discover only its documented-looking local pipe namespace;
                // the server PID/image is checked after connect, before HTTP headers.
                foreach (string activePipe in activePipes)
                    if (!activePipe.Equals(pipeName, StringComparison.OrdinalIgnoreCase))
                        controllers.Add(new LocalProxyController(new Uri("http://127.0.0.1/"), secret, activePipe));
                // A stale release pipe must not consume its one-second connect
                // timeout before every poll of the active alpha/beta controller.
                if (configuredPipe && !activePipes.Contains(pipeName, StringComparer.OrdinalIgnoreCase))
                    controllers.Add(new LocalProxyController(new Uri("http://127.0.0.1/"), secret, pipeName));
                if (TryLoopbackController(endpoint, out var address)) controllers.Add(new LocalProxyController(address, secret));
            }
            catch { /* Optional local configuration; never inspect subscription files. */ }
            foreach (var controller in controllers) yield return controller;
        }
    }

    private static HttpClient CreateClient(string pipeName)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        };
        if (pipeName.Length > 0)
        {
            handler.ConnectCallback = async (_, cancellationToken) =>
            {
                // Server "." is fixed: configuration can never select a remote pipe server.
                var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    if (!IsMihomoPipeServer(stream.SafePipeHandle)) throw new IOException("Unexpected local controller server.");
                    return stream;
                }
                catch { stream.Dispose(); throw; }
            };
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
    }

    private static IEnumerable<string> ActiveMihomoPipes()
    {
        string[] paths;
        try { paths = Directory.GetFiles(@"\\.\pipe\"); }
        catch { yield break; }
        foreach (string path in paths)
            if (TryLocalPipe(path, out string name) && Regex.IsMatch(name,
                @"\Averge-mihomo-(?:production|release|alpha|beta)-[a-fA-F0-9]{32,64}\z", RegexOptions.CultureInvariant))
                yield return name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    private static bool IsMihomoPipeServer(SafePipeHandle pipe)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipe, out uint processId) || processId == 0) return false;
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName.ToLowerInvariant() is "mihomo" or "verge-mihomo" or "verge-mihomo-alpha" or "verge-mihomo-beta";
        }
        catch { return false; }
    }

    private static bool TryLocalPipe(string value, out string pipeName)
    {
        pipeName = "";
        const string prefix = @"\\.\pipe\";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string name = value[prefix.Length..];
        if (name.Length is < 1 or > 240 || name is "." or ".." ||
            name.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_'))) return false;
        pipeName = name;
        return true;
    }

    private static bool TryLoopbackController(string text, out Uri address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith(':')) text = "127.0.0.1" + text;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || parsed.Port is < 1 or > 65535 ||
            parsed.UserInfo.Length > 0 || parsed.AbsolutePath != "/" || parsed.Query.Length > 0 || parsed.Fragment.Length > 0) return false;
        string host = parsed.Host.Trim('[', ']');
        // Never resolve a hostname: localhost is rewritten to a numeric loopback.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "0.0.0.0") host = "127.0.0.1";
        else if (host == "::") host = "::1";
        if (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip)) return false;
        address = new UriBuilder(parsed) { Host = ip.ToString() }.Uri;
        return true;
    }

    private static string ReadYamlScalar(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            // YAML's ordinary double-quoted controller/secret values are JSON compatible.
            for (int i = 1; i < value.Length; i++)
                if (value[i] == '"' && value[i - 1] != '\\')
                {
                    try { return JsonSerializer.Deserialize<string>(value[..(i + 1)]) ?? ""; }
                    catch { return ""; }
                }
            return "";
        }
        if (value.StartsWith('\''))
        {
            var text = new StringBuilder();
            for (int i = 1; i < value.Length; i++)
            {
                if (value[i] != '\'') { text.Append(value[i]); continue; }
                if (i + 1 < value.Length && value[i + 1] == '\'') { text.Append('\''); i++; continue; }
                return text.ToString();
            }
            return "";
        }
        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0) value = value[..comment];
        return value is "null" or "~" ? "" : value.Trim();
    }

    private static bool MatchesProcess(int pid, string reportedName, string reportedPath)
    {
        string expected = string.IsNullOrEmpty(reportedName) ? Path.GetFileName(reportedPath) : Path.GetFileName(reportedName);
        if (string.IsNullOrEmpty(expected)) return true;
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!Path.GetFileNameWithoutExtension(expected).Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(reportedPath))
            {
                try
                {
                    string? actual = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(actual) && !actual.Equals(reportedPath, StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch { /* Process name remains a useful check when its image path is protected. */ }
            }
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static bool TryAddress(string text, out IPAddress address)
    {
        if (!IPAddress.TryParse(text, out address!)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return true;
    }

    private static string GetString(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";

    private static int GetInt(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var field)) return 0;
        if (field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int number)) return number;
        return field.ValueKind == JsonValueKind.String && int.TryParse(field.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        _stop.Cancel();
        _controllers.Dispose();
        _clients.Dispose();
        lock (_publishGate)
        {
            _cache.Clear();
            _types.Clear();
        }
        _stop.Dispose();
    }
}

internal sealed record LocalProxyController(Uri Address, string Secret, string PipeName = "");

/// <summary>
/// Prefer the last working candidate from the current discovery snapshot. Keep
/// alternate credentials for the same endpoint available for configuration rotation.
/// </summary>
internal sealed class ProxyControllerPollOrder
{
    private readonly record struct Candidate(string PipeName, string Address, string Secret);
    private Candidate? _preferred;
    private List<Candidate>? _previousCandidates;

    private static Candidate Key(LocalProxyController controller) => new(
        controller.PipeName.ToUpperInvariant(), controller.Address.AbsoluteUri, controller.Secret);

    public IReadOnlyList<LocalProxyController> Order(IReadOnlyList<LocalProxyController> controllers)
    {
        var seen = new HashSet<Candidate>();
        var ordered = new List<LocalProxyController>(controllers.Count);
        var candidates = new List<Candidate>(controllers.Count);
        foreach (var controller in controllers)
        {
            var candidate = Key(controller);
            if (!seen.Add(candidate)) continue;
            ordered.Add(controller);
            candidates.Add(candidate);
        }
        // A real configuration change restores discovery's configured ordering;
        // equivalent five-second rediscoveries keep the working fast path.
        if (_previousCandidates != null && !candidates.SequenceEqual(_previousCandidates)) _preferred = null;
        _previousCandidates = candidates;
        int preferredIndex = _preferred.HasValue ? candidates.IndexOf(_preferred.Value) : -1;
        if (preferredIndex > 0)
        {
            // Always select the current object, never an old successful snapshot.
            var preferred = ordered[preferredIndex];
            ordered.RemoveAt(preferredIndex);
            ordered.Insert(0, preferred);
        }
        return ordered;
    }

    public void Succeeded(LocalProxyController controller) => _preferred = Key(controller);
}

/// <summary>Discovery runs on the poll worker; disposal never waits for file or pipe enumeration.</summary>
internal sealed class ProxyControllerDiscoveryCache : IDisposable
{
    private sealed record Snapshot(DateTime ReadUtc, IReadOnlyList<LocalProxyController> Controllers);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);
    private readonly Func<IEnumerable<LocalProxyController>> _read;
    private readonly object _gate = new();
    private Snapshot _snapshot = new(DateTime.MinValue, Array.Empty<LocalProxyController>());
    private int _refreshing;
    private int _disposed;

    public ProxyControllerDiscoveryCache(Func<IEnumerable<LocalProxyController>> read) => _read = read;

    public IReadOnlyList<LocalProxyController> Get(DateTime nowUtc)
    {
        if (Volatile.Read(ref _disposed) != 0) return Array.Empty<LocalProxyController>();
        var previous = Volatile.Read(ref _snapshot);
        if (nowUtc >= previous.ReadUtc && nowUtc - previous.ReadUtc < DiscoveryInterval) return previous.Controllers;
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return previous.Controllers;
        try
        {
            var controllers = _read().ToArray();
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return Array.Empty<LocalProxyController>();
                Volatile.Write(ref _snapshot, new Snapshot(nowUtc, controllers));
                return controllers;
            }
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        lock (_gate) Volatile.Write(ref _snapshot, new Snapshot(DateTime.MinValue, Array.Empty<LocalProxyController>()));
    }
}

/// <summary>Bounded transport reuse. Credentials belong to requests, never pooled handlers.</summary>
internal sealed class ProxyHttpClientPool : IDisposable
{
    internal sealed class Entry(HttpClient client)
    {
        public HttpClient Client { get; } = client;
        public int Requests;
        public long LastUsed;
    }

    internal sealed class Lease(ProxyHttpClientPool owner, Entry entry) : IDisposable
    {
        private ProxyHttpClientPool? _owner = owner;
        public HttpClient Client => entry.Client;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(entry);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _clients = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _sequence;
    private bool _disposed;

    public ProxyHttpClientPool(int capacity) => _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public Lease? TryAcquire(string key, Func<HttpClient> create)
    {
        HttpClient? retired = null;
        Lease lease;
        lock (_gate)
        {
            if (_disposed) return null;
            if (!_clients.TryGetValue(key, out var entry))
            {
                string? oldest = null;
                if (_clients.Count >= _capacity)
                {
                    oldest = _clients.Where(p => p.Value.Requests == 0).OrderBy(p => p.Value.LastUsed)
                        .Select(p => p.Key).FirstOrDefault();
                    // Optional hints may skip one poll rather than interrupt an active request.
                    if (oldest == null) return null;
                }
                entry = new Entry(create());
                if (oldest != null)
                {
                    retired = _clients[oldest].Client;
                    _clients.Remove(oldest);
                }
                _clients[key] = entry;
            }
            entry.Requests++;
            entry.LastUsed = ++_sequence;
            lease = new Lease(this, entry);
        }
        retired?.Dispose();
        return lease;
    }

    private void Release(Entry entry)
    {
        lock (_gate) entry.Requests--;
    }

    public void Dispose()
    {
        HttpClient[] clients;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            clients = _clients.Values.Select(entry => entry.Client).ToArray();
            _clients.Clear();
        }
        foreach (var client in clients) client.Dispose();
    }
}

/// <summary>
/// Controller-scoped, short-lived name/type strings only. No proxy response
/// documents, node endpoints, credentials or node configuration are retained here.
/// </summary>
internal sealed class ProxyTypeCache
{
    private sealed record State(DateTime LastAttemptUtc, DateTime? LastSuccessUtc,
        Dictionary<string, string> Types, bool InFlight);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private const int MaximumControllers = 32;
    private const int MaximumNodes = 16384;
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _controllers = new(StringComparer.Ordinal);

    public bool TryBeginRefresh(string controllerKey, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_controllers.TryGetValue(controllerKey, out var previous))
            {
                if (previous.InFlight || nowUtc < previous.LastAttemptUtc ||
                    nowUtc - previous.LastAttemptUtc < RefreshInterval) return false;
                _controllers[controllerKey] = previous with { LastAttemptUtc = nowUtc, InFlight = true };
                return true;
            }
            if (_controllers.Count >= MaximumControllers)
            {
                var oldest = _controllers.Where(p => !p.Value.InFlight).OrderBy(p => p.Value.LastAttemptUtc)
                    .Select(p => p.Key).FirstOrDefault();
                if (oldest == null) return false;
                _controllers.Remove(oldest);
            }
            _controllers[controllerKey] = new State(nowUtc, null, new(StringComparer.Ordinal), true);
            return true;
        }
    }

    public void CompleteRefresh(string controllerKey, IReadOnlyDictionary<string, string>? types, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_controllers.TryGetValue(controllerKey, out var previous) || !previous.InFlight) return;
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            if (types != null)
                foreach (var pair in types.Take(MaximumNodes))
                    if (ValidName(pair.Key) && ValidType(pair.Value)) strings[pair.Key] = pair.Value;
            _controllers[controllerKey] = previous with { LastSuccessUtc = types == null ? null : nowUtc,
                Types = strings, InFlight = false };
        }
    }

    public string Lookup(string controllerKey, string node, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_controllers.TryGetValue(controllerKey, out var state) || !state.LastSuccessUtc.HasValue ||
                nowUtc < state.LastSuccessUtc || nowUtc - state.LastSuccessUtc > RefreshInterval) return "Unknown";
            return state.Types.TryGetValue(node, out var type) ? type : "Unknown";
        }
    }

    public static Dictionary<string, string>? ParseTypes(JsonElement root) => ParseTypes(root, null);

    public static Dictionary<string, string>? ParseTypes(JsonElement? globalRoot, JsonElement? providerRoot)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        bool validShape = false;
        void Add(string name, JsonElement proxy)
        {
            if (++count > MaximumNodes || !ValidName(name) || ambiguous.Contains(name) || proxy.ValueKind != JsonValueKind.Object ||
                !proxy.TryGetProperty("type", out var field) || field.ValueKind != JsonValueKind.String) return;
            string type = field.GetString() ?? "";
            if (!ValidType(type)) return;
            if (strings.TryGetValue(name, out var previous) && previous != type)
            {
                strings.Remove(name);
                ambiguous.Add(name);
            }
            else strings[name] = type;
        }
        if (globalRoot is JsonElement global && global.ValueKind == JsonValueKind.Object &&
            global.TryGetProperty("proxies", out var proxies) && proxies.ValueKind == JsonValueKind.Object)
        {
            validShape = true;
            foreach (var proxy in proxies.EnumerateObject())
            {
                if (count >= MaximumNodes) break;
                Add(proxy.Name, proxy.Value);
            }
        }
        if (providerRoot is JsonElement provider && provider.ValueKind == JsonValueKind.Object &&
            provider.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Object)
        {
            validShape = true;
            foreach (var entry in providers.EnumerateObject())
            {
                if (count >= MaximumNodes) break;
                if (entry.Value.ValueKind != JsonValueKind.Object || !entry.Value.TryGetProperty("proxies", out var nodes) ||
                    nodes.ValueKind != JsonValueKind.Array) continue;
                foreach (var node in nodes.EnumerateArray())
                {
                    if (count >= MaximumNodes) break;
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("name", out var name) ||
                        name.ValueKind != JsonValueKind.String) continue;
                    Add(name.GetString() ?? "", node);
                }
            }
        }
        return validShape ? strings : null;
    }

    public void Clear()
    {
        lock (_gate) _controllers.Clear();
    }

    private static bool ValidName(string name) => name.Length is > 0 and <= 1024;
    private static bool ValidType(string type) => type.Length is > 0 and <= 64 &&
        type.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
