using System.Text.Json;

namespace CodexTraffic;

public static class LocalProxyHintCacheSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        DateTime origin = new(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
        DateTime At(double seconds) => origin.AddSeconds(seconds);
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        ProxyConnectionHint Connection(string id, double started, double observed, string node = "node-a",
            string host = "chatgpt.com", string destination = "", int pid = 42, string outboundType = "") =>
            new(id, At(started), "127.0.0.1", 51000, pid, "codex.exe", "",
                new ProxyHint(host, destination, 443, "group → " + node, "test controller", At(observed), node,
                    OutboundType: outboundType));
        ProxyHint? Lookup(ProxyHintCache cache, double? at, double now = 1.8, int pid = 42,
            string local = "127.0.0.1", string remote = "127.0.0.1", int port = 7897,
            bool processMatches = true) => cache.Lookup(pid, local, 51000, remote, port, At(now),
                at.HasValue ? At(at.Value) : null, (_, _, _) => processMatches);

        var cache = new ProxyHintCache();
        cache.Update(new[] { Connection("old", 0, 1) }, At(1));
        Check(Lookup(cache, 0.2)?.Node == "node-a" && Lookup(cache, 1.2)?.Node == "node-a",
            "A fresh current hint accepts events after its connection start, including events after the latest poll");
        Check(Lookup(cache, -0.1) == null,
            "A connection cannot annotate events from before its controller-reported start");
        cache.Update(Array.Empty<ProxyConnectionHint>(), At(1.5));
        Check(Lookup(cache, 1.2)?.Node == "node-a" && Lookup(cache, null) == null,
            "Recently disappeared hints remain available to event-time flushes but not current connection views");
        Check(Lookup(cache, 1.5) == null && Lookup(cache, 1.6) == null,
            "A disappeared connection cannot annotate events at or after the first absent snapshot");
        Check(Lookup(cache, 1.2, now: 6.01) == null,
            "Recent proxy hints expire five seconds after their last live observation");

        cache = new ProxyHintCache();
        cache.Update(new[] { Connection("old", 0, 1) }, At(1));
        cache.Update(new[] { Connection("new", 1.2, 1.5, "node-b") }, At(1.5));
        Check(Lookup(cache, 1.1)?.Node == "node-a",
            "A reused source port's new node never replaces an earlier connection's event hint");
        Check(Lookup(cache, 1.3) == null,
            "Overlapping old-close and new-start observation windows reject ambiguous source-port reuse");
        Check(Lookup(cache, 1.6)?.Node == "node-b" && Lookup(cache, null)?.Node == "node-b",
            "Events after the first old-connection absence and current views use the new connection");
        Check(Lookup(cache, 1.6, pid: 43) == null && Lookup(cache, 1.6, processMatches: false) == null,
            "PID conflicts and failed process identity checks still reject proxy hints");

        cache = new ProxyHintCache();
        cache.Update(new[] { Connection("a", 0, 1), Connection("b", 0, 1, "node-b", "api.example.test") }, At(1));
        Check(Lookup(cache, 0.5) == null && Lookup(cache, null) == null,
            "Simultaneous different targets on a reused source endpoint are never guessed");

        cache = new ProxyHintCache();
        cache.Update(new[] { Connection("direct", 0, 1, destination: "1.1.1.1") }, At(1));
        Check(Lookup(cache, 0.5, local: "::ffff:127.0.0.1", remote: "1.1.1.1", port: 443) != null &&
            Lookup(cache, 0.5, remote: "8.8.8.8", port: 443) == null &&
            Lookup(cache, 0.5, remote: "1.1.1.1", port: 80) == null &&
            Lookup(cache, 0.5, local: "127.0.0.2") == null,
            "Source addresses normalize mapped IPv4 while direct destination IP and port must agree");

        cache = new ProxyHintCache();
        cache.Update(new[] { Connection("", 0, 1) with { StartedUtc = null } }, At(1));
        Check(Lookup(cache, null) != null && Lookup(cache, 0.5) == null,
            "Connections without a reliable id and start remain current-only and cannot annotate timed events");
        cache.Update(Array.Empty<ProxyConnectionHint>(), At(1.5));
        Check(Lookup(cache, null) == null && Lookup(cache, 0.5) == null,
            "Anonymous connections are discarded when they disappear");

        cache = new ProxyHintCache();
        cache.Update(new[] { Connection("same", 0, 1) }, At(1));
        cache.Update(new[] { Connection("same", 0, 1.5, "node-b") }, At(1.5));
        Check(Lookup(cache, 1.2)?.Node == "node-a" && Lookup(cache, 1.6)?.Node == "node-b",
            "Changed metadata on the same identity does not rewrite events before the change was observed");
        Check(Lookup(cache, 2, now: 1.8) == null,
            "Future event timestamps cannot use present proxy hints");
        cache.Clear();
        Check(Lookup(cache, null) == null,
            "Clearing the proxy hint cache removes all current and retained connections");

        cache = new ProxyHintCache();
        cache.Update(new[] { Connection("typed", 0, 1, outboundType: "Unknown") }, At(1));
        cache.Update(new[] { Connection("typed", 0, 1.5, outboundType: "Direct") }, At(1.5));
        Check(Lookup(cache, 1.2)?.OutboundType == "Unknown" && Lookup(cache, 1.6)?.OutboundType == "Direct",
            "A newly available outbound type does not rewrite events before that metadata was observed");

        using var typesJson = JsonDocument.Parse("""
            {"proxies":{
                "my-local-route":{"type":"Direct","server":"ignored.invalid","password":"not-retained"},
                "my-node":{"type":"Shadowsocks","udp":true},
                "group":{"type":"Selector","now":"my-node","all":["my-node"]},
                "blocked":{"type":"Reject"},
                "bad-number":{"type":123},
                "bad-string":{"type":"Direct with configuration"},
                "no-type":{"server":"ignored.invalid"}
            }}
            """);
        var parsed = ProxyTypeCache.ParseTypes(typesJson.RootElement)!;
        Check(parsed.Count == 4 && parsed["my-local-route"] == "Direct" && parsed["my-node"] == "Shadowsocks" &&
            parsed["group"] == "Selector" && parsed["blocked"] == "Reject",
            "Proxy metadata parsing retains only exact name/type strings, including custom-named Direct and group types");
        using var malformedShape = JsonDocument.Parse("{\"proxies\":[]}");
        using var duplicateTypes = JsonDocument.Parse("{\"proxies\":{\"same\":{\"type\":\"Direct\"},\"same\":{\"type\":\"Vless\"}}}");
        Check(ProxyTypeCache.ParseTypes(malformedShape.RootElement) == null &&
            ProxyTypeCache.ParseTypes(duplicateTypes.RootElement)!.Count == 0,
            "Malformed proxy map shapes and duplicate conflicting types remain unknown");

        using var providerJson = JsonDocument.Parse("""
            {"providers":{
                "provider-a":{"type":"Proxy","vehicleType":"HTTP","testUrl":"ignored.invalid","proxies":[
                    {"name":"provider-vless","type":"Vless","server":"ignored.invalid"},
                    {"name":"my-node","type":"Shadowsocks"},
                    {"name":"my-local-route","type":"Vless"},
                    {"name":"conflicting-leaf","type":"Direct"}]},
                "provider-b":{"proxies":[
                    {"name":"provider-vless","type":"Vless"},
                    {"name":"conflicting-leaf","type":"Vless"},
                    {"name":123,"type":"Direct"}]}
            }}
            """);
        var merged = ProxyTypeCache.ParseTypes(typesJson.RootElement, providerJson.RootElement)!;
        Check(merged.Count == 4 && merged["provider-vless"] == "Vless" && merged["my-node"] == "Shadowsocks" &&
            !merged.ContainsKey("my-local-route") && !merged.ContainsKey("conflicting-leaf") && !merged.ContainsKey("provider-a"),
            "Global and provider leaf types merge by name, deduplicate equal types and reject conflicting types without retaining provider fields");
        var providerOnly = ProxyTypeCache.ParseTypes(null, providerJson.RootElement)!;
        Check(providerOnly.Count == 3 && providerOnly["provider-vless"] == "Vless",
            "Provider leaf metadata remains available when the optional global proxy endpoint is unavailable");
        using var malformedProviders = JsonDocument.Parse("{\"providers\":[]}");
        Check(ProxyTypeCache.ParseTypes(typesJson.RootElement, malformedProviders.RootElement)!.Count == 4 &&
            ProxyTypeCache.ParseTypes(null, malformedProviders.RootElement) == null,
            "An unsupported provider metadata shape does not discard a valid global type map");
        using var conflictingGlobalRepeat = JsonDocument.Parse("{\"providers\":{\"p\":{\"proxies\":[{\"name\":\"same\",\"type\":\"Direct\"}]}}}");
        Check(ProxyTypeCache.ParseTypes(duplicateTypes.RootElement, conflictingGlobalRepeat.RootElement)!.Count == 0,
            "A name already ambiguous in global metadata cannot be resurrected by a provider entry");

        var typeCache = new ProxyTypeCache();
        Check(typeCache.TryBeginRefresh("controller-a", At(0)) &&
            !typeCache.TryBeginRefresh("controller-a", At(6)),
            "Only one metadata refresh may be in flight for a controller");
        typeCache.CompleteRefresh("controller-a", parsed, At(0));
        parsed["my-local-route"] = "Vless";
        Check(typeCache.Lookup("controller-a", "my-local-route", At(1)) == "Direct" &&
            typeCache.Lookup("controller-b", "my-local-route", At(1)) == "Unknown" &&
            typeCache.Lookup("controller-a", "MY-LOCAL-ROUTE", At(1)) == "Unknown",
            "Outbound type cache copies strings and isolates controllers and case-sensitive node names");
        Check(!typeCache.TryBeginRefresh("controller-a", At(4.99)) &&
            typeCache.Lookup("controller-a", "my-local-route", At(5.01)) == "Unknown" &&
            typeCache.TryBeginRefresh("controller-a", At(5.01)),
            "Proxy type metadata is requested at most once per five seconds and stale types expire");
        typeCache.CompleteRefresh("controller-a", null, At(5.1));
        Check(typeCache.Lookup("controller-a", "my-local-route", At(5.2)) == "Unknown" &&
            !typeCache.TryBeginRefresh("controller-a", At(6)),
            "Failed proxy metadata requests clear only that controller's types and preserve refresh throttling");
        Check(typeCache.TryBeginRefresh("controller-b", At(6)),
            "Another controller's proxy type refresh has an independent schedule");
        typeCache.CompleteRefresh("controller-b", new Dictionary<string, string> { ["my-local-route"] = "Vless" }, At(6));
        Check(typeCache.Lookup("controller-b", "my-local-route", At(6.1)) == "Vless" &&
            typeCache.Lookup("controller-a", "my-local-route", At(6.1)) == "Unknown" &&
            typeCache.Lookup("controller-b", "unlisted-node", At(6.1)) == "Unknown",
            "Same-named nodes use their own controller type and missing nodes explicitly remain Unknown");
        typeCache.Clear();
        Check(typeCache.Lookup("controller-b", "my-local-route", At(6.2)) == "Unknown",
            "Disposal clears cached proxy metadata strings");
        return checks;
    }
}
