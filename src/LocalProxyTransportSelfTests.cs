using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace CodexTraffic;

public static class LocalProxyTransportSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }

        DateTime origin = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        int discoveries = 0;
        using (var discovery = new ProxyControllerDiscoveryCache(() =>
        {
            discoveries++;
            return new[] { new LocalProxyController(new Uri("http://127.0.0.1:12345/"), "test-rotation-" + discoveries) };
        }))
        {
            var initial = discovery.Get(origin);
            for (int poll = 1; poll < 10; poll++) discovery.Get(origin.AddMilliseconds(poll * 500));
            Check(discoveries == 1,
                "Ten 500 ms connection polls share one five-second controller and pipe discovery");
            var rotated = discovery.Get(origin.AddSeconds(5));
            Check(discoveries == 2 && initial[0].Address == rotated[0].Address && initial[0].Secret != rotated[0].Secret,
                "Controller discovery refreshes at five seconds and picks up credential rotation at the same endpoint");
            discovery.Get(origin.AddSeconds(4));
            Check(discoveries == 3, "A backwards clock change expires controller discovery rather than extending its cache indefinitely");
            discovery.Dispose();
            Check(discovery.Get(origin.AddSeconds(10)).Count == 0 && discoveries == 3,
                "Disposed controller discovery clears its snapshot and performs no further reads");
        }

        int emptyDiscoveries = 0;
        using (var empty = new ProxyControllerDiscoveryCache(() =>
        {
            emptyDiscoveries++;
            return Array.Empty<LocalProxyController>();
        }))
        {
            empty.Get(origin);
            empty.Get(origin.AddSeconds(4));
            Check(emptyDiscoveries == 1, "An absent proxy controller is also cached for five seconds");
        }

        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var slow = new ProxyControllerDiscoveryCache(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Discovery test release timed out");
                return new[] { new LocalProxyController(new Uri("http://127.0.0.1:12345/"), "test-only") };
            });
            var read = Task.Run(() => slow.Get(origin));
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(5)), "A controller discovery can be in flight on a worker");
                slow.Dispose();
                Check(!read.IsCompleted && slow.Get(origin).Count == 0,
                    "Disposing the discovery cache does not wait for an in-flight directory scan");
            }
            finally { release.Set(); }
            Check(read.GetAwaiter().GetResult().Count == 0,
                "An in-flight discovery cannot repopulate its cache after disposal");
        }

        var handlers = new List<RecordingHandler>();
        HttpClient Create()
        {
            var handler = new RecordingHandler();
            handlers.Add(handler);
            return new HttpClient(handler);
        }
        using (var pool = new ProxyHttpClientPool(2))
        {
            using var activeA = pool.TryAcquire("endpoint-a", Create)!;
            using var sameA = pool.TryAcquire("endpoint-a", Create)!;
            Check(ReferenceEquals(activeA.Client, sameA.Client) && handlers.Count == 1,
                "Connection and concurrent metadata requests reuse one client for the same controller");
            using var activeB = pool.TryAcquire("endpoint-b", Create)!;
            Check(pool.TryAcquire("endpoint-c", Create) == null && handlers.Count == 2 && handlers.All(handler => !handler.IsDisposed),
                "A full transport pool skips optional work instead of evicting clients with in-flight requests");
            activeA.Dispose();
            Check(pool.TryAcquire("endpoint-c", Create) == null,
                "A shared client remains protected until every outstanding request lease is released");
            sameA.Dispose();
            using var activeC = pool.TryAcquire("endpoint-c", Create)!;
            Check(handlers.Count == 3 && handlers[0].IsDisposed && !handlers[1].IsDisposed,
                "A full transport pool evicts and disposes only an idle controller");
            activeC.Dispose();
            for (int endpoint = 0; endpoint < 40; endpoint++)
            {
                using var next = pool.TryAcquire("replacement-" + endpoint, Create)!;
            }
            Check(handlers.Count(handler => !handler.IsDisposed) == 2 && !handlers[1].IsDisposed,
                "Repeated controller changes keep the live client count bounded while preserving an active request");
            pool.Dispose();
            Check(handlers.All(handler => handler.IsDisposed) && pool.TryAcquire("after-dispose", Create) == null,
                "Transport disposal closes active and idle clients and prevents later acquisition");
        }

        using (var pool = new ProxyHttpClientPool(1))
        {
            foreach (string value in new[] { "test-old", "test-new" })
            {
                using var lease = pool.TryAcquire("same-controller", Create)!;
                using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/connections");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
                using var response = lease.Client.SendAsync(request).GetAwaiter().GetResult();
                Check(lease.Client.DefaultRequestHeaders.Authorization == null,
                    "Pooled transport does not retain credentials in default request headers");
            }
            Check(handlers[^1].Authorizations.SequenceEqual(new[] { "test-old", "test-new" }),
                "A reused client sends each request's current authorization rather than its original credential");
        }

        using (var pool = new ProxyHttpClientPool(1))
        {
            var blocking = new CancellationHandler();
            using var lease = pool.TryAcquire("in-flight", () => new HttpClient(blocking))!;
            var request = lease.Client.GetAsync("http://127.0.0.1/connections");
            Check(blocking.Started, "A pooled transport may have an HTTP request in flight during disposal");
            pool.Dispose();
            bool canceled = false;
            try { request.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Dispose(); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Transport disposal cancels an in-flight HTTP request without waiting for it to finish");
        }
        return checks;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }
        public List<string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Authorizations.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public bool Started { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started = true;
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("An infinite request completed without cancellation");
        }
    }
}
