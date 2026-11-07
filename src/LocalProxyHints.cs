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
    private sealed record Controller(Uri Address, string Secret, string PipeName = "");
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Threading.Timer _timer;
    private readonly ProxyHintCache _cache = new();
    private readonly ProxyTypeCache _types = new();
    private int _refreshing;
    private int _disposed;

    public LocalProxyHints()
    {
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// Matches a complete source endpoint. Event-time lookups may use connections
    /// seen within five seconds, bounded by controller start/absence observations.
    /// Ambiguous matches, stale snapshots and process conflicts yield no hint.
    /// A loopback proxy next hop or a fake-IP may differ from the reported target.
    /// </summary>
    public ProxyHint? Lookup(int pid, string localAddress, int localPort, string remoteAddress, int remotePort,
        DateTime? observedAtUtc = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        return _cache.Lookup(pid, localAddress, localPort, remoteAddress, remotePort,
            DateTime.UtcNow, observedAtUtc, MatchesProcess);
    }

    private async Task RefreshAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            foreach (var controller in ReadControllers())
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
                    if (_types.TryBeginRefresh(controllerKey, observed)) _ = RefreshTypesAsync(controller, controllerKey);
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
                    if (Volatile.Read(ref _disposed) == 0) _cache.Update(hints, observed);
                    return;
                }
                catch { /* Missing, disabled or inaccessible controller is normal. No credentials or response bodies are logged. */ }
            }
            _cache.Update(Array.Empty<ProxyConnectionHint>(), DateTime.UtcNow);
        }
        catch { _cache.Update(Array.Empty<ProxyConnectionHint>(), DateTime.UtcNow); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    private static string ControllerKey(Controller controller) => controller.PipeName + "|" + controller.Address.AbsoluteUri;

    private async Task RefreshTypesAsync(Controller controller, string controllerKey)
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
            if (Volatile.Read(ref _disposed) == 0) _types.CompleteRefresh(controllerKey, types, DateTime.UtcNow);
        }
        catch
        {
            // A disabled/unsupported metadata endpoint leaves the type unknown.
            // It never discards or blocks the separately captured connections.
            _types.CompleteRefresh(controllerKey, null, DateTime.UtcNow);
        }
    }

    private async Task<JsonDocument?> ReadOptionalControllerJsonAsync(Controller controller, string path)
    {
        try { return await ReadControllerJsonAsync(controller, path).ConfigureAwait(false); }
        catch { return null; }
    }

    private async Task<JsonDocument?> ReadControllerJsonAsync(Controller controller, string path)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller.Address, path));
        if (!string.IsNullOrEmpty(controller.Secret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", controller.Secret);
        using var client = CreateClient(controller);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes) return null;
        using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[16384];
        int count;
        while ((count = await body.ReadAsync(bytes.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException();
            buffer.Write(bytes, 0, count);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    private static IEnumerable<Controller> ReadControllers()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (string directory in new[] { "io.github.clash-verge-rev.clash-verge-rev", "io.github.clash-verge-rev.clash-verge" })
        foreach (string name in new[] { "clash-verge.yaml", "config.yaml" })
        {
            var controllers = new List<Controller>();
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
                string[] activePipes = ActiveMihomoPipes().ToArray();
                if (configuredPipe && activePipes.Contains(pipeName, StringComparer.OrdinalIgnoreCase))
                    controllers.Add(new Controller(new Uri("http://127.0.0.1/"), secret, pipeName));
                // Verge can leave a release pipe in config after switching to an alpha
                // core. Discover only its documented-looking local pipe namespace;
                // the server PID/image is checked after connect, before HTTP headers.
                foreach (string activePipe in activePipes)
                    if (!activePipe.Equals(pipeName, StringComparison.OrdinalIgnoreCase))
                        controllers.Add(new Controller(new Uri("http://127.0.0.1/"), secret, activePipe));
                // A stale release pipe must not consume its one-second connect
                // timeout before every poll of the active alpha/beta controller.
                if (configuredPipe && !activePipes.Contains(pipeName, StringComparer.OrdinalIgnoreCase))
                    controllers.Add(new Controller(new Uri("http://127.0.0.1/"), secret, pipeName));
                if (TryLoopbackController(endpoint, out var address)) controllers.Add(new Controller(address, secret));
            }
            catch { /* Optional local configuration; never inspect subscription files. */ }
            foreach (var controller in controllers) yield return controller;
        }
    }

    private static HttpClient CreateClient(Controller controller)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        if (controller.PipeName.Length > 0)
        {
            handler.ConnectCallback = async (_, cancellationToken) =>
            {
                // Server "." is fixed: configuration can never select a remote pipe server.
                var stream = new NamedPipeClientStream(".", controller.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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
        _cache.Clear();
        _types.Clear();
        _stop.Dispose();
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
