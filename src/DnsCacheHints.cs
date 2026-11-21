using System.Net;

namespace CodexTraffic;

// Read existing Windows cache only. Never makes a DNS query or sends hostnames elsewhere.
public sealed class DnsCacheHints : IDisposable
{
    internal const int MaximumRecords = 8192;
    private const int RefreshMilliseconds = 60000;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private readonly Func<IReadOnlyList<DnsCacheRecord>?> _read;
    private readonly Func<long> _now;
    private Dictionary<string, DnsHintSet> _hints = new();
    private int _refreshing;
    private int _polling;
    private volatile bool _disposed;

    public DnsCacheHints() : this(WindowsDnsCacheReader.Read, () => Environment.TickCount64, true) { }

    internal DnsCacheHints(Func<IReadOnlyList<DnsCacheRecord>?> read, Func<long> now, bool startTimer = false)
    {
        _read = read;
        _now = now;
        _polling = startTimer ? 1 : 0;
        _timer = new System.Threading.Timer(_ => Refresh(), null,
            startTimer ? 0 : Timeout.Infinite, startTimer ? RefreshMilliseconds : Timeout.Infinite);
    }

    internal bool IsPolling => Volatile.Read(ref _polling) == 1 && !_disposed;

    /// <summary>Pauses or resumes the cache read; resuming reads immediately.</summary>
    internal void SetPolling(bool enabled)
    {
        if (_disposed) return;
        int desired = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref _polling, desired) == desired) return;
        try { _timer.Change(enabled ? 0 : Timeout.Infinite, enabled ? RefreshMilliseconds : Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    public string Lookup(string ip)
    {
        lock (_gate)
        {
            if (_disposed || !_hints.TryGetValue(ip, out var names)) return "";
            long now = _now();
            return now < names.FirstExpiry ? names.Joined :
                string.Join(" / ", names.Names.Where(x => x.ExpiresAt > now).Select(x => x.Name));
        }
    }

    internal void Refresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            // Measure TTL from before enumeration, never extending a cache record while reading it.
            long observedAt = _now();
            var records = _read();
            var hints = new Dictionary<string, List<DnsHint>>();
            foreach (var row in records?.Take(MaximumRecords) ?? Enumerable.Empty<DnsCacheRecord>())
            {
                string name = row.Name.TrimEnd('.');
                if (row.Type is not (1 or 28) || row.Status != 0 || row.TimeToLive == 0 ||
                    name.Length is 0 or > 253 || name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
                    !IPAddress.TryParse(row.Address, out var address)) continue;
                if ((row.Type == 1) != (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) continue;
                string key = address.ToString();
                if (!hints.TryGetValue(key, out var names)) hints[key] = names = new();
                long expiresAt = observedAt + Math.Min(row.TimeToLive * 1000L, RefreshMilliseconds);
                int duplicate = names.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (duplicate >= 0)
                    names[duplicate] = names[duplicate] with { ExpiresAt = Math.Min(names[duplicate].ExpiresAt, expiresAt) };
                else if (names.Count < 3) names.Add(new(name, expiresAt));
            }
            lock (_gate)
                if (!_disposed) _hints = hints.ToDictionary(x => x.Key, x => new DnsHintSet(
                    x.Value.ToArray(), string.Join(" / ", x.Value.Select(n => n.Name)), x.Value.Min(n => n.ExpiresAt)));
        }
        catch
        {
            // DNS hints are optional. Failed refreshes must not leave stale names attached forever.
            lock (_gate) _hints.Clear();
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        lock (_gate) _hints.Clear();
    }

    private sealed record DnsHint(string Name, long ExpiresAt);
    private sealed record DnsHintSet(DnsHint[] Names, string Joined, long FirstExpiry);
}

internal sealed record DnsCacheRecord(string Name, string Address, uint TimeToLive, ushort Type, uint Status);
