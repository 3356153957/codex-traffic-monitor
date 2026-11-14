using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

internal static class ProxyControllerPollingSelfTests
{
    internal static IReadOnlyList<string> Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }

        using (var unavailable = new ControllerFixture { Mode = ControllerFixture.NoResponse })
        using (var healthy = new ControllerFixture())
        using (var hints = new LocalProxyHints(() => new[]
        {
            unavailable.Controller(), unavailable.Controller(), healthy.Controller()
        }, false))
        {
            var watch = Stopwatch.StartNew();
            await hints.RefreshAsync();
            var initial = watch.Elapsed;
            Check(initial >= TimeSpan.FromMilliseconds(700) && unavailable.ConnectionRequests == 1 && healthy.ConnectionRequests == 1 &&
                hints.Lookup(0, "127.0.0.1", 51000, "127.0.0.1", 7897)?.Host == "optimized.test",
                "A real unresponsive loopback controller times out once despite duplicate discovery entries, then the final controller publishes usable hints");

            watch.Restart();
            await hints.RefreshAsync();
            Check(watch.Elapsed < TimeSpan.FromMilliseconds(750) && unavailable.ConnectionRequests == 1 && healthy.ConnectionRequests == 2,
                "The next real HTTP poll prioritizes the last successful controller and avoids the preceding one-second timeout");

            healthy.Mode = ControllerFixture.Unavailable;
            unavailable.Mode = ControllerFixture.Healthy;
            await hints.RefreshAsync();
            Check(healthy.ConnectionRequests == 3 && unavailable.ConnectionRequests == 2 &&
                hints.Lookup(0, "127.0.0.1", 51000, "127.0.0.1", 7897)?.Host == "optimized.test",
                "Failure of the preferred controller falls back to the complete current candidate list and publishes the recovered controller");
            await hints.RefreshAsync();
            Check(healthy.ConnectionRequests == 3 && unavailable.ConnectionRequests == 3,
                "A recovered fallback becomes the preferred controller on the following poll");
        }

        using (var controller = new ControllerFixture { RequiredSecret = "fixture-current" })
        using (var hints = new LocalProxyHints(() => new[]
        {
            controller.Controller("fixture-old"), controller.Controller("fixture-current"), controller.Controller("fixture-current")
        }, false))
        {
            await hints.RefreshAsync();
            Check(controller.ConnectionRequests == 2 && controller.AuthorizedConnectionRequests == 1,
                "Candidates at one endpoint retain alternate credentials while identical candidates are deduplicated");
            await hints.RefreshAsync();
            Check(controller.ConnectionRequests == 3 && controller.AuthorizedConnectionRequests == 2,
                "The exact successful current credential is preferred on the next real HTTP request");
            controller.RequiredSecret = "fixture-old";
            await hints.RefreshAsync();
            Check(controller.ConnectionRequests == 5 && controller.AuthorizedConnectionRequests == 3,
                "Credential rejection of a preferred endpoint still tries the other discovered credential and recovers");
        }

        var order = new ProxyControllerPollOrder();
        var old = new LocalProxyController(new Uri("http://127.0.0.1:12345/"), "fixture-old");
        var replacement = old with { Secret = "fixture-current" };
        var other = new LocalProxyController(new Uri("http://127.0.0.1:12346/"), "");
        order.Succeeded(old);
        var rotated = order.Order(new[] { other, replacement });
        Check(rotated.Count == 2 && ReferenceEquals(rotated[0], other) && ReferenceEquals(rotated[1], replacement) &&
            order.Order(new[] { other }).SequenceEqual(new[] { other }),
            "Rediscovery uses only current candidate objects and never resurrects a removed controller or its old credential");
        var currentObject = replacement with { };
        order.Order(new[] { other, replacement });
        order.Succeeded(replacement);
        Check(ReferenceEquals(order.Order(new[] { other, currentObject })[0], currentObject),
            "Preference selects the rediscovered current configuration object instead of retaining the previous one");
        var newlyConfigured = new LocalProxyController(new Uri("http://127.0.0.1:12347/"), "");
        Check(ReferenceEquals(order.Order(new[] { newlyConfigured, other, currentObject })[0], newlyConfigured),
            "A changed discovery configuration restores its new ordering even if the old preferred controller remains available");
        var pipe = old with { PipeName = "fixture-pipe" };
        Check(order.Order(new[] { pipe, pipe with { PipeName = "FIXTURE-PIPE" } }).Count == 1,
            "Case variants of one Windows named pipe are deduplicated within the same poll");

        using (var controller = new ControllerFixture())
        using (var hints = new LocalProxyHints(() => new[] { controller.Controller() }, true))
        {
            var deadline = Stopwatch.StartNew();
            while (controller.ConnectionRequests < 5 && deadline.Elapsed < TimeSpan.FromSeconds(4))
                await Task.Delay(20);
            var observations = controller.ObservationTicks.ToArray();
            double averageMs = observations.Length < 5 ? double.PositiveInfinity :
                Stopwatch.GetElapsedTime(observations[0], observations[4]).TotalMilliseconds / 4;
            Check(observations.Length >= 5 && averageMs is >= 250 and < 850,
                "The real production timer continues polling successful local controller connections every 500 milliseconds");
        }
        return checks;
    }

    private sealed class ControllerFixture : IDisposable
    {
        public const int Healthy = 0, NoResponse = 1, Unavailable = 2;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _requests = new();
        private readonly Task _accept;
        private readonly string _body;
        private int _mode, _connectionRequests, _authorizedConnectionRequests;
        private string _requiredSecret = "";

        public int Mode { get => Volatile.Read(ref _mode); set => Volatile.Write(ref _mode, value); }
        public string RequiredSecret { get => Volatile.Read(ref _requiredSecret); set => Volatile.Write(ref _requiredSecret, value); }
        public int ConnectionRequests => Volatile.Read(ref _connectionRequests);
        public int AuthorizedConnectionRequests => Volatile.Read(ref _authorizedConnectionRequests);
        public ConcurrentQueue<long> ObservationTicks { get; } = new();

        public ControllerFixture()
        {
            _body = JsonSerializer.Serialize(new
            {
                connections = new[] { new { id = "fixture", start = DateTime.UtcNow.AddSeconds(-1),
                    metadata = new { sourceIP = "127.0.0.1", sourcePort = 51000,
                        destinationIP = "203.0.113.1", destinationPort = 443, host = "optimized.test" } } }
            });
            _listener.Start();
            _accept = AcceptAsync();
        }

        public LocalProxyController Controller(string secret = "") => new(
            new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/"), secret);

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _requests.Add(ServeAsync(client));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string? line = await reader.ReadLineAsync(_stop.Token);
                bool connection = line?.StartsWith("GET /connections ", StringComparison.Ordinal) == true;
                bool authorized = RequiredSecret.Length == 0;
                for (int header = 0; header < 32; header++)
                {
                    line = await reader.ReadLineAsync(_stop.Token);
                    if (string.IsNullOrEmpty(line)) break;
                    if (line.Equals("Authorization: Bearer " + RequiredSecret, StringComparison.Ordinal)) authorized = true;
                }
                if (connection)
                {
                    ObservationTicks.Enqueue(Stopwatch.GetTimestamp());
                    Interlocked.Increment(ref _connectionRequests);
                    if (authorized) Interlocked.Increment(ref _authorizedConnectionRequests);
                }
                int mode = Mode;
                if (mode == NoResponse) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                string status = !authorized ? "401 Unauthorized" : mode == Unavailable ? "503 Service Unavailable" : "200 OK";
                string body = !authorized || mode == Unavailable ? "{}" : connection ? _body : "{\"proxies\":{},\"providers\":{}}";
                byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " +
                    Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
                await stream.WriteAsync(response, _stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _accept.GetAwaiter().GetResult();
            Task.WhenAll(_requests.ToArray()).GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
