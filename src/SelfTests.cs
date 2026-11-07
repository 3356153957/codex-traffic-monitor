using System.IO;
using System.Net;
using System.Text.Json;

namespace CodexTraffic;

public static class SelfTests
{
    public static int Run(string? outputPath)
    {
        var checks = new List<string>();
        string directory = Path.Combine(Path.GetTempPath(), "CodexTrafficTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        try
        {
            using var geo = new GeoIpHints();
            Check(string.IsNullOrEmpty(geo.Lookup("127.0.0.1").Region) && string.IsNullOrEmpty(geo.Lookup("198.18.1.71").Region), "Offline geography never assigns a country to a loopback or fake-IP next hop");
            Check(!geo.IsAvailable || !string.IsNullOrEmpty(geo.Lookup("17.0.0.1").Region), "Installed local MMDB can resolve a public IP without a network query");
            Check(GeoIpHints.CountryName("CLOUDFLARE") == "", "A database's CDN network tag is never interpreted as a country code");
            checks.AddRange(AnalyticsSelfTests.Run());
            checks.AddRange(ConnectionPresentationSelfTests.Run());
            checks.AddRange(LocalProxyHintCacheSelfTests.Run());
            checks.AddRange(InstallerIntegrationSelfTests.Run());
            checks.AddRange(TrafficScopeSelfTests.Run());
            var startupDocument = System.Xml.Linq.XDocument.Parse(StartupManager.BuildTaskXml(Path.Combine(directory, "含 空格", "CodexTrafficMonitor.exe"), "S-1-5-21-123-456-789-1001"));
            System.Xml.Linq.XNamespace taskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            Check(startupDocument.Descendants(taskNs + "LogonTrigger").Count() == 1 && !startupDocument.Descendants(taskNs + "BootTrigger").Any() && startupDocument.Descendants(taskNs + "UserId").All(x => x.Value == "S-1-5-21-123-456-789-1001"), "Startup task triggers only for the selected user's login");
            Check(startupDocument.Descendants(taskNs + "RunLevel").Single().Value == "HighestAvailable" && startupDocument.Descendants(taskNs + "Arguments").Single().Value == "--start --minimized" && startupDocument.Descendants(taskNs + "ExecutionTimeLimit").Single().Value == "PT0S", "Startup task requests unattended ETW monitoring without a fixed recording time limit");
            var normalizedTask = System.Xml.Linq.XDocument.Parse(StartupManager.BuildTaskXml(Environment.ProcessPath!, StartupManager.UserSid));
            normalizedTask.Descendants(taskNs + "LogonTrigger").Single().Element(taskNs + "UserId")!.Value = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            Check(StartupManager.ParseStatus(normalizedTask.ToString(), false, StartupManager.UserSid, "test").Error.Length == 0, "Regression: Windows-normalized COMPUTER\\user login trigger matches the registered user's SID");
            Check(GeoIpHints.NodeFromChain("策略组 → 地区组 → sample node") == "sample node" && new CaptureOptions().IncludeChildren, "Derived tools default on and proxy node is distinct from a policy group");
            Check(AddressClassifier.Classify(IPAddress.Parse("127.0.0.1")).Contains("回环"), "IPv4 loopback is not a public destination");
            Check(AddressClassifier.Classify(IPAddress.Parse("::1")).Contains("回环"), "IPv6 loopback is not a public destination");
            Check(AddressClassifier.Classify(IPAddress.Parse("198.19.255.254")).Contains("虚拟"), "198.18/15 fake-IP range is explicit");
            Check(AddressClassifier.Classify(IPAddress.Parse("192.168.2.3")).Contains("私有"), "RFC1918 private route");
            Check(AddressClassifier.Classify(IPAddress.Parse("fc00::2")).Contains("私有"), "IPv6 private route");
            Check(AddressClassifier.Classify(IPAddress.Parse("::ffff:127.0.0.1")).Contains("回环"), "IPv4 mapped IPv6 loopback");
            Check(AddressClassifier.Endpoint("2001:db8::1", 443) == "[2001:db8::1]:443", "IPv6 endpoint formatting");
            var tcpLoopbackReceive = EndpointResolver.Resolve("TCP", false, "127.0.0.1", 8855, "127.0.0.1", 5847);
            Check(tcpLoopbackReceive.LocalPort == 8855 && tcpLoopbackReceive.RemotePort == 5847, "Regression: real ETW TCP receive keeps short-lived loopback client's local and server ports");
            var udpLoopbackReceive = EndpointResolver.Resolve("UDP", false, "127.0.0.1", 64250, "127.0.0.1", 63045);
            Check(udpLoopbackReceive.LocalPort == 63045 && udpLoopbackReceive.RemotePort == 64250, "Regression: UDP receive uses packet direction instead of TCP field convention");
            var ledger = new TrafficLedger();
            var now = DateTime.UtcNow;
            ledger.Add(now, 11, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 53211, "127.0.0.1", 7897, 1024, true);
            ledger.Add(now, 11, "codex.exe", "C:\\codex.exe", "core", "TCP", "127.0.0.1", 53211, "127.0.0.1", 7897, 8192, false);
            ledger.Add(now, 11, "codex.exe", "C:\\codex.exe", "core", "UDP6", "::1", 53212, "::1", 7897, 2048, true);
            Check(ledger.UploadBytes == 3072 && ledger.DownloadBytes == 8192, "Upload/download byte accounting across TCP and UDP6");
            Check(ledger.Targets().Count == 2 && ledger.Targets()[0].DownloadBytes == 8192, "Protocol-specific destinations and total ordering");
            var first = ledger.Drain();
            Check(first.Sum(x => x.UploadBytes) == 3072 && first.Sum(x => x.DownloadBytes) == 8192 && ledger.Drain().Count == 0, "Interval drain does not repeat counted bytes");
            ledger.Annotate(first.Single(x => x.Protocol == "TCP"), new ProxyHint("example.test", "203.0.113.9", 443, "test route", "代理报告", now));
            Check(ledger.Targets().Count(x => x.Hostname == "example.test") == 1 && ledger.UploadBytes == 3072 && ledger.DownloadBytes == 8192, "Proxy-reported names annotate one socket without replacing ETW byte accounting");
            string log;
            using (var store = new SessionStore(directory, false, true))
            {
                log = store.LogFile;
                store.Append(first);
                store.Update(ledger.UploadBytes, ledger.DownloadBytes, 3, ended: true);
            }
            var saved = File.ReadAllLines(log).Select(x => JsonSerializer.Deserialize<FlowRecord>(x)!).ToList();
            Check(saved.Sum(x => x.UploadBytes) == 3072 && saved.Sum(x => x.DownloadBytes) == 8192, "JSONL persists directional totals and endpoints");
            var history = SessionStore.History(directory);
            Check(history.Count == 1 && history[0].UploadBytes == 3072, "History reload after process restart");
            var rows = ledger.Targets();
            rows[0].Process = "=HYPERLINK(\"file\")";
            string csv = Path.Combine(directory, "test.csv");
            SessionStore.WriteCsv(csv, rows);
            string text = File.ReadAllText(csv);
            Check(text.StartsWith("Process,PID,Protocol,") && text.Contains("\"'=HYPERLINK(\"\"file\"\")\""), "CSV has raw bytes and escaped formula-like process names");
            var catalog = new ProcessCatalog();
            catalog.Refresh();
            Check(!catalog.IsTracked(Environment.ProcessId, true), "Monitor and its own tooling are excluded from accounting");
            var connections = ConnectionReader.GetTcpConnections();
            Check(connections.All(x => x.LocalPort >= 0 && x.LocalPort <= 65535 && x.RemotePort >= 0 && x.RemotePort <= 65535), "Native IPv4/IPv6 connection rows parse valid network byte-order ports");
            var report = new { success = true, checks, count = checks.Count, trackedCoreProcesses = catalog.List(false).Count, trackedWithTools = catalog.List(true).Count, tcpConnections = connections.Count, testDirectory = directory };
            File.WriteAllText(outputPath ?? Path.Combine(directory, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(outputPath ?? Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { success = false, checks, error = e.ToString(), testDirectory = directory }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
