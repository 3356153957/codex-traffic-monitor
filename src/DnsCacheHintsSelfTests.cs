namespace CodexTraffic;

public static class DnsCacheHintsSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        long now = 1000;
        IReadOnlyList<DnsCacheRecord>? records = new DnsCacheRecord[]
        {
            new("api.example.test.", "203.0.113.1", 3, 1, 0),
            new("API.EXAMPLE.TEST", "203.0.113.1", 2, 1, 0),
            new("second.example.test", "203.0.113.1", 10, 1, 0),
            new("third.example.test", "203.0.113.1", 10, 1, 0),
            new("fourth.example.test", "203.0.113.1", 10, 1, 0),
            new("ipv6.example.test", "2001:0db8::1", 600, 28, 0),
            new("expired.example.test", "203.0.113.2", 0, 1, 0),
            new("failed.example.test", "203.0.113.3", 10, 1, 9003),
            new("alias.example.test", "203.0.113.4", 10, 5, 0),
            new("wrong-family.example.test", "203.0.113.5", 10, 28, 0),
            new("bad-address.example.test", "not-an-address", 10, 1, 0),
            new("bad\nname.example.test", "203.0.113.6", 10, 1, 0),
            new(new string('a', 254), "203.0.113.7", 10, 1, 0),
        };
        using var cache = new DnsCacheHints(() => records, () => now);
        cache.Refresh();
        Check(cache.Lookup("203.0.113.1") == "api.example.test / second.example.test / third.example.test" &&
            cache.Lookup("2001:db8::1") == "ipv6.example.test",
            "DNS hints normalize IPs and trailing dots, deduplicate names ignoring case and retain at most three names per IP");
        Check(Enumerable.Range(2, 6).All(i => cache.Lookup("203.0.113." + i) == "") && cache.Lookup("not-an-address") == "",
            "Only successful unexpired A/AAAA records with valid names and matching address families become DNS hints");
        now = 3000;
        Check(cache.Lookup("203.0.113.1") == "second.example.test / third.example.test",
            "DNS names expire at their own TTL between refreshes, including the shortest duplicate TTL");
        now = 61000;
        Check(cache.Lookup("2001:db8::1") == "",
            "Long DNS TTLs cannot keep a missed or failed refresh's snapshot alive beyond one minute");

        cache.Refresh();
        records = Array.Empty<DnsCacheRecord>();
        cache.Refresh();
        Check(cache.Lookup("2001:db8::1") == "", "An empty Windows DNS cache removes old hints");
        records = new[] { new DnsCacheRecord("fresh.example.test", "203.0.113.1", 100, 1, 0) };
        cache.Refresh();
        records = null;
        cache.Refresh();
        Check(cache.Lookup("203.0.113.1") == "", "An unavailable DNS cache reader degrades to missing hints without stale names");

        using var unavailable = new DnsCacheHints(() => throw new InvalidOperationException("fixture failure"), () => now);
        unavailable.Refresh();
        Check(unavailable.Lookup("203.0.113.1") == "", "A DNS provider exception is optional and never escapes into traffic collection");
        records = Enumerable.Range(0, DnsCacheHints.MaximumRecords + 1)
            .Select(i => new DnsCacheRecord("row" + i + ".example.test", "2001:db8::" + (i + 1).ToString("x"), 100, 28, 0)).ToArray();
        cache.Refresh();
        Check(cache.Lookup("2001:db8::2000") != "" && cache.Lookup("2001:db8::2001") == "",
            "DNS snapshot retention has a fixed record bound even when the provider returns a large cache");

        int calls = 0;
        DnsCacheHints? reentrant = null;
        using (reentrant = new DnsCacheHints(() => { calls++; reentrant!.Refresh(); return records; }, () => now))
            reentrant.Refresh();
        Check(calls == 1, "Overlapping DNS refreshes cannot start extra cache reads");

        DnsCacheHints? disposedDuringRead = null;
        disposedDuringRead = new DnsCacheHints(() => { disposedDuringRead!.Dispose(); return records; }, () => now);
        disposedDuringRead.Refresh();
        Check(disposedDuringRead.Lookup("2001:db8::1") == "", "Disposal clears DNS hints and an in-flight refresh cannot repopulate them");
        return checks;
    }
}
