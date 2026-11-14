using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

internal static class BoundedProxyResponseSelfTests
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
        const int maximum = 8 * 1024 * 1024;
        using var hints = new LocalProxyHints(() => Array.Empty<LocalProxyController>(), false);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pool = (ProxyHttpClientPool)typeof(LocalProxyHints).GetField("_clients", flags)!.GetValue(hints)!;
        var read = typeof(LocalProxyHints).GetMethod("ReadControllerJsonAsync", flags)!;
        var controller = new LocalProxyController(new Uri("http://127.0.0.1:19871/"), "");
        var handler = new FixtureHandler();
        using var lease = pool.TryAcquire("|" + controller.Address.AbsoluteUri, () => new HttpClient(handler));
        Task<JsonDocument?> Read() => (Task<JsonDocument?>)read.Invoke(hints, new object[] { controller, "/connections" })!;
        byte[] Document(int size)
        {
            var bytes = new byte[size];
            Array.Fill(bytes, (byte)' ');
            bytes[0] = (byte)'{'; bytes[1] = (byte)'}';
            return bytes;
        }
        async Task<bool> Rejected<T>() where T : Exception
        {
            try { using var ignored = await Read(); return false; }
            catch (T) { return true; }
        }

        handler.Body = Encoding.UTF8.GetBytes("{\"connections\":[]}");
        using (var json = await Read())
            Check(json?.RootElement.GetProperty("connections").GetArrayLength() == 0,
                "A nonseekable response without Content-Length and with text/plain content remains valid JSON");
        handler.Body = Document(maximum);
        handler.DeclaredLength = maximum;
        using (var json = await Read())
            Check(json?.RootElement.ValueKind == JsonValueKind.Object && handler.LastStream!.BytesRead == maximum,
                "A response exactly at the eight MiB limit is accepted after confirming EOF");
        handler.Body = Document(maximum + 1);
        handler.DeclaredLength = null;
        Check(await Rejected<InvalidDataException>() && handler.LastStream!.BytesRead == maximum + 1,
            "An oversized response without Content-Length stops after at most one excess byte");
        handler.DeclaredLength = 2;
        Check(await Rejected<InvalidDataException>(),
            "A misleading small declared length cannot bypass the actual response byte limit");
        handler.DeclaredLength = maximum + 1;
        using (var json = await Read())
            Check(json == null && handler.LastStream!.BytesRead == 0,
                "A declared oversized response is rejected before reading its body");
        handler.DeclaredLength = null;
        handler.Body = Array.Empty<byte>();
        Check(await Rejected<JsonException>(), "An empty response is rejected without publishing metadata");
        handler.Body = Encoding.UTF8.GetBytes("{broken-json");
        Check(await Rejected<JsonException>(), "Malformed JSON remains a failed controller response");
        handler.Slow = true;
        Check(await Rejected<OperationCanceledException>(), "The existing one-second timeout cancels a stalled response body read");
        handler.Slow = false;
        handler.Body = Encoding.UTF8.GetBytes("{\"connections\":[]}");
        using (var json = await Read())
            Check(json != null, "A controller can recover on the next read after cancellation");

        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        using var bounded = new BoundedReadStream(input, 2);
        byte[] buffer = new byte[2];
        Check(bounded.Read(buffer, 0, 2) == 2 && bounded.Read(Span<byte>.Empty) == 0,
            "Synchronous and zero-length reads honor the same bound");
        bool rejected = false;
        try { bounded.Read(buffer.AsSpan()); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Synchronous reads also reject the first byte beyond the bound");
        return checks;
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        internal byte[] Body = Array.Empty<byte>();
        internal long? DeclaredLength;
        internal bool Slow;
        internal FragmentedStream? LastStream;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastStream = new FragmentedStream(Body, Slow);
            var content = new StreamContent(LastStream);
            content.Headers.ContentType = new("text/plain");
            content.Headers.ContentLength = DeclaredLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FragmentedStream(byte[] bytes, bool slow) : Stream
    {
        internal int BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(count, 7331), bytes.Length - BytesRead);
            bytes.AsSpan(BytesRead, take).CopyTo(buffer.AsSpan(offset, count));
            BytesRead += take;
            return take;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slow) await Task.Delay(Timeout.Infinite, cancellationToken);
            int take = Math.Min(Math.Min(buffer.Length, 7331), bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, take).CopyTo(buffer);
            BytesRead += take;
            return take;
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
