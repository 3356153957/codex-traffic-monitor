using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;

namespace CodexTraffic;

// Read existing Windows cache only. Never makes a DNS query or sends hostnames elsewhere.
public sealed class DnsCacheHints : IDisposable
{
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private Dictionary<string, string> _hints = new();
    private int _refreshing;
    private bool _disposed;

    public DnsCacheHints() => _timer = new System.Threading.Timer(_ => Refresh(), null, 0, 60000);

    public string Lookup(string ip)
    {
        lock (_gate) return _hints.TryGetValue(ip, out var name) ? name : "";
    }

    private void Refresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("$ErrorActionPreference='Stop'; Get-DnsClientCache | Where-Object { $_.Type -in 1,28 } | Select-Object Entry,Data,TimeToLive | ConvertTo-Json -Compress");
            using var process = Process.Start(info);
            if (process == null) return;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(8000)) { process.Kill(true); return; }
            string raw = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(raw)) return;
            using var json = JsonDocument.Parse(raw);
            var rows = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : new[] { json.RootElement };
            var hints = new Dictionary<string, List<string>>();
            foreach (var row in rows)
            {
                string? ip = row.GetProperty("Data").GetString(), name = row.GetProperty("Entry").GetString();
                if (name == null || !IPAddress.TryParse(ip, out var parsed)) continue;
                string key = parsed.ToString();
                if (!hints.TryGetValue(key, out var names)) hints[key] = names = new();
                if (names.Count < 3 && !names.Contains(name)) names.Add(name);
            }
            lock (_gate) _hints = hints.ToDictionary(x => x.Key, x => string.Join(" / ", x.Value));
        }
        catch { /* DNS hints are optional; collection never depends on them. */ }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    public void Dispose() { _disposed = true; _timer.Dispose(); }
}
