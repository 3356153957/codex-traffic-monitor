using System.Net;
using System.Net.Sockets;

namespace CodexTraffic;

internal sealed record ProxyConnectionHint(string Identity, DateTime? StartedUtc,
    string SourceAddress, int SourcePort, int Pid, string Process, string ProcessPath, ProxyHint Hint);

/// <summary>
/// Retains only recently observed controller connections, not historical guesses.
/// Controller start times prevent a reused source port's new connection from
/// annotating an earlier event. Polling cannot observe connections between polls.
/// </summary>
internal sealed class ProxyHintCache
{
    private sealed record Entry(ProxyConnectionHint Connection, DateTime ValidFromUtc, DateTime? MissingUtc);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(5);
    private const int MaximumEntries = 16384;
    private readonly object _gate = new();
    private Entry[] _entries = Array.Empty<Entry>();

    public void Update(IEnumerable<ProxyConnectionHint> snapshot, DateTime observedUtc)
    {
        lock (_gate)
        {
            var previous = Volatile.Read(ref _entries)
                .Where(e => IsFresh(e.Connection.Hint.ObservedUtc, observedUtc)).ToArray();
            var byLifetime = previous.Where(e => HasLifetime(e.Connection))
                .GroupBy(e => (e.Connection.Identity, e.Connection.StartedUtc))
                .ToDictionary(g => g.Key, g => g.ToArray());
            var next = new List<Entry>();
            var retained = new HashSet<Entry>();
            foreach (var connection in snapshot.Take(MaximumEntries))
            {
                bool hasLifetime = HasLifetime(connection);
                Entry[] versions = hasLifetime && byLifetime.TryGetValue((connection.Identity, connection.StartedUtc), out var found)
                    ? found : Array.Empty<Entry>();
                var matching = versions.FirstOrDefault(e => SameTarget(e.Connection, connection));
                // If metadata changes within a connection, do not apply the new
                // target/chain to events from before that version was observed.
                bool changed = versions.Any(e => !SameTarget(e.Connection, connection));
                DateTime validFrom = matching?.ValidFromUtc ??
                    (changed ? observedUtc : connection.StartedUtc ?? observedUtc);
                if (matching != null) retained.Add(matching);
                next.Add(new Entry(connection, validFrom, null));
            }
            foreach (var entry in previous)
            {
                // An id and controller-provided start time are both required for
                // retention. Anonymous current snapshots remain current-only.
                if (!HasLifetime(entry.Connection) || retained.Contains(entry)) continue;
                next.Add(entry with { MissingUtc = entry.MissingUtc ?? observedUtc });
            }
            Volatile.Write(ref _entries, next.OrderByDescending(e => e.MissingUtc == null)
                .ThenByDescending(e => e.Connection.Hint.ObservedUtc).Take(MaximumEntries).ToArray());
        }
    }

    public ProxyHint? Lookup(int pid, string localAddress, int localPort, string remoteAddress, int remotePort,
        DateTime nowUtc, DateTime? observedAtUtc, Func<int, string, string, bool> matchesProcess)
    {
        if (localPort is < 1 or > 65535 || !TryAddress(localAddress, out var local) ||
            !TryAddress(remoteAddress, out var remote)) return null;
        DateTime? eventUtc = observedAtUtc?.ToUniversalTime();
        if (eventUtc.HasValue && !IsFresh(eventUtc.Value, nowUtc)) return null;
        Entry? result = null;
        foreach (var entry in Volatile.Read(ref _entries))
        {
            var candidate = entry.Connection;
            if (!IsFresh(candidate.Hint.ObservedUtc, nowUtc)) continue;
            if (eventUtc.HasValue)
            {
                if (!HasLifetime(candidate) || eventUtc < entry.ValidFromUtc ||
                    (entry.MissingUtc.HasValue && eventUtc >= entry.MissingUtc)) continue;
            }
            else if (entry.MissingUtc.HasValue) continue;
            if (candidate.SourcePort != localPort || !TryAddress(candidate.SourceAddress, out var source) ||
                !source.Equals(local)) continue;
            if (candidate.Pid > 0 && (pid <= 0 || candidate.Pid != pid)) continue;
            if (!matchesProcess(pid, candidate.Process, candidate.ProcessPath)) continue;
            // Loopback/fake-IP are proxy next hops. Public/LAN direct targets
            // must agree with the controller's destination address and port.
            if (!IPAddress.IsLoopback(remote))
            {
                if (candidate.Hint.DestinationPort != remotePort) continue;
                if (!IsFakeAddress(remote) && (!TryAddress(candidate.Hint.DestinationAddress, out var destination) ||
                    !destination.Equals(remote))) continue;
            }
            // Lifecycle intervals can overlap between the last live snapshot
            // and the first absent snapshot. Never choose one on that overlap.
            if (result != null && (!SameLifetime(result.Connection, candidate) ||
                !SameTarget(result.Connection, candidate))) return null;
            result = entry;
        }
        return result?.Connection.Hint;
    }

    public void Clear()
    {
        lock (_gate) Volatile.Write(ref _entries, Array.Empty<Entry>());
    }

    private static bool IsFresh(DateTime observed, DateTime now) =>
        now >= observed && now - observed <= MaximumAge;
    private static bool HasLifetime(ProxyConnectionHint connection) =>
        connection.Identity.Length > 0 && connection.StartedUtc.HasValue &&
        connection.StartedUtc <= connection.Hint.ObservedUtc;
    private static bool SameLifetime(ProxyConnectionHint left, ProxyConnectionHint right) =>
        left.Identity == right.Identity && left.StartedUtc == right.StartedUtc;
    private static bool SameTarget(ProxyConnectionHint left, ProxyConnectionHint right) =>
        left.SourceAddress == right.SourceAddress && left.SourcePort == right.SourcePort && left.Pid == right.Pid &&
        left.Process == right.Process && left.ProcessPath == right.ProcessPath &&
        left.Hint.Host == right.Hint.Host && left.Hint.DestinationAddress == right.Hint.DestinationAddress &&
        left.Hint.DestinationPort == right.Hint.DestinationPort && left.Hint.Chain == right.Hint.Chain &&
        left.Hint.Node == right.Hint.Node && left.Hint.OutboundType == right.Hint.OutboundType;
    private static bool TryAddress(string value, out IPAddress address)
    {
        if (!IPAddress.TryParse(value, out address!)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return true;
    }
    private static bool IsFakeAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 198 && bytes[1] is 18 or 19;
    }
}
