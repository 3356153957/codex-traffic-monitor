namespace CodexTraffic;

internal static class AccuracyProxySelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        var origin = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        DateTime At(double seconds) => origin.AddSeconds(seconds);
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        ProxyConnectionHint Hint(string id, double start, double observed, string host = "a.test", string node = "node-a") =>
            new(id, At(start), "127.0.0.1", 51000, 42, "codex.exe", "",
                new ProxyHint(host, "203.0.113.1", 443, "group → " + node, "test", At(observed), node, OutboundType: "Vless"));
        ProxyHint? Range(ProxyHintCache cache, double first, double last, double now = 3) =>
            cache.Lookup(42, "127.0.0.1", 51000, "127.0.0.1", 7897, At(now), At(first), (_, _, _) => true, At(last));

        var cache = new ProxyHintCache();
        cache.Update(new[] { Hint("one", 0, 0.5) }, At(0.5));
        cache.Update(new[] { Hint("one", 0, 1.5) }, At(1.5));
        Check(Range(cache, 0.2, 1.8)?.Host == "a.test", "One continuous proxy version can annotate the entire pending byte interval");
        Check(Range(cache, 1.8, 0.2) == null && Range(cache, -3, 0.2) == null,
            "Reversed or stale pending event ranges cannot gain a proxy destination");

        cache.Update(new[] { Hint("two", 1.9, 2, "b.test", "node-b") }, At(2));
        Check(Range(cache, 0.2, 2.2) == null,
            "Source-port reuse within one flush interval preserves bytes without assigning the whole interval to its first host");
        Check(Range(cache, 0.2, 1.8)?.Host == "a.test" && Range(cache, 2.1, 2.5)?.Host == "b.test",
            "Intervals on either side of an observed connection change retain their own proxy evidence");

        cache = new ProxyHintCache();
        cache.Update(new[] { Hint("one", 0, 0.5) }, At(0.5));
        cache.Update(new[] { Hint("one", 0, 1, "b.test", "node-b") }, At(1));
        cache.Update(new[] { Hint("one", 0, 2) }, At(2));
        Check(Range(cache, 0.2, 2.2) == null && Range(cache, 1.2, 1.8)?.Host == "b.test" && Range(cache, 2.1, 2.5)?.Host == "a.test",
            "A to B to A proxy changes never restore an old validity interval over the intervening target");

        cache = new ProxyHintCache();
        cache.Update(new[] { Hint("one", 0, 0.5) }, At(0.5));
        cache.Update(Array.Empty<ProxyConnectionHint>(), At(1));
        cache.Update(new[] { Hint("one", 0, 2) }, At(2));
        Check(Range(cache, 0.2, 2.2) == null && Range(cache, 1.2, 1.8) == null && Range(cache, 2.1, 2.5)?.Host == "a.test",
            "A reappearing proxy version cannot erase an observed absence or annotate its gap");

        cache = new ProxyHintCache();
        cache.Update(new[] { Hint("one", 0, 1), Hint("two", 0.8, 1, "b.test", "node-b") }, At(1));
        Check(Range(cache, 0.2, 1.5) == null,
            "A partially overlapping conflicting version invalidates a range even when another covers both endpoints");
        Check(cache.Lookup(42, "127.0.0.1", 51000, "127.0.0.1", 7897, At(2), null, (_, _, _) => true) == null,
            "Ambiguous current connection lookups remain conservative alongside range validation");
        cache = new ProxyHintCache();
        cache.Update(new[] { Hint("one", 0, 1) }, At(1));
        cache.Update(Array.Empty<ProxyConnectionHint>(), At(2));
        cache.Update(Array.Empty<ProxyConnectionHint>(), At(7));
        cache.Update(new[] { Hint("one", 0, 8) }, At(8));
        Check(Range(cache, 7.5, 7.6, 8.2) == null && Range(cache, 8.1, 8.2, 8.3)?.Host == "a.test",
            "Expiring old cache entries does not let a returning controller backfill an observed absence");
        return checks;
    }
}
