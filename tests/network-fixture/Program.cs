using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

// Diagnostic fixture only. The client apphost is deliberately named codex.exe so
// the unmodified production catalog and ETW accounting path can observe it.
// Run only in the isolated diagnostic procedure; never beside a normal recording.
internal static class Program
{
    private const int UploadBytes = 256 * 1024;
    private const int DownloadBytes = 128 * 1024;
    private const int UdpUploadPacket = 4096;
    private const int UdpDownloadPacket = 2048;
    private const int UdpPackets = UploadBytes / UdpUploadPacket;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Channel(string Name, string Protocol, string Address, int Port,
        int ExpectedUploadBytes, int ExpectedDownloadBytes);
    private sealed record Result(string Name, string Protocol, string Address, int RemotePort,
        int LocalPort, long UploadBytes, long DownloadBytes);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] is not ("--server" or "--client") ||
            args.Length == 3 && args[2] != "--ipv4-only")
        {
            Console.Error.WriteLine("Usage: codex --server|--client <isolated-run-directory> [--ipv4-only]");
            return 2;
        }
        var directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        var resultFile = Path.Combine(directory, args[0] == "--server" ? "server-result.json" : "client-result.json");
        var ipv4Only = args.Length == 3;
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if (args[0] == "--server") await RunServer(directory, ipv4Only, lifetime.Token);
            else await RunClient(directory, ipv4Only, lifetime.Token);
            return 0;
        }
        catch (Exception exception)
        {
            JsonElement? completedChannels = null;
            string? currentChannel = null;
            var progressPath = Path.Combine(directory, "client-progress.json");
            if (args[0] == "--client" && File.Exists(progressPath))
            {
                using var progress = JsonDocument.Parse(File.ReadAllText(progressPath));
                completedChannels = progress.RootElement.GetProperty("channels").Clone();
                currentChannel = progress.RootElement.GetProperty("currentChannel").GetString();
            }
            Save(resultFile, new { success = false, pid = Environment.ProcessId, ipv4Only,
                channels = completedChannels, currentChannel, completedUtc = DateTime.UtcNow, error = exception.ToString() });
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task RunServer(string directory, bool ipv4Only, CancellationToken cancellation)
    {
        var resources = new List<IDisposable>();
        var channels = new List<Channel>();
        var operations = new List<Task<Result>>();
        string? ipv6Skip = null;
        try
        {
            AddFamily(IPAddress.Loopback, "4");
            if (ipv4Only) ipv6Skip = "IPv4Only explicitly selected; IPv6 was not tested.";
            else if (Socket.OSSupportsIPv6)
            {
                try { AddFamily(IPAddress.IPv6Loopback, "6"); }
                catch (SocketException exception) when (exception.SocketErrorCode is
                    SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable or SocketError.ProtocolNotSupported)
                { ipv6Skip = exception.Message; }
            }
            else ipv6Skip = "Windows reports no IPv6 socket support.";
            Save(Path.Combine(directory, "server-ready.json"), new
            {
                pid = Environment.ProcessId, channels, ipv6Skip, ipv4Only,
                udpRule = "64 stop-and-wait exchanges: each 4096-byte upload is confirmed by one 2048-byte response; timeout fails instead of changing the expected counts."
            });
            var results = await Task.WhenAll(operations);
            Save(Path.Combine(directory, "server-result.json"), new { success = true, pid = Environment.ProcessId, channels = results, ipv6Skip, ipv4Only });
        }
        finally { foreach (var resource in resources) resource.Dispose(); }

        void AddFamily(IPAddress address, string suffix)
        {
            var tcp = new TcpListener(address, 0);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) tcp.Server.DualMode = false;
            tcp.Start();
            resources.Add(tcp);
            var udp = new UdpClient(address.AddressFamily);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) udp.Client.DualMode = false;
            resources.Add(udp);
            udp.Client.Bind(new IPEndPoint(address, 0));
            var tcpChannel = new Channel("TCP" + suffix, suffix == "6" ? "TCP6" : "TCP", address.ToString(),
                ((IPEndPoint)tcp.LocalEndpoint).Port, UploadBytes, DownloadBytes);
            var udpChannel = new Channel("UDP" + suffix, suffix == "6" ? "UDP6" : "UDP", address.ToString(),
                ((IPEndPoint)udp.Client.LocalEndPoint!).Port, UploadBytes, DownloadBytes);
            channels.Add(tcpChannel);
            channels.Add(udpChannel);
            operations.Add(ServeTcp(tcp, tcpChannel, cancellation));
            operations.Add(ServeUdp(udp, udpChannel, cancellation));
        }
    }

    private static async Task<Result> ServeTcp(TcpListener listener, Channel channel, CancellationToken cancellation)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellation);
        client.NoDelay = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        operation.CancelAfter(TimeSpan.FromSeconds(20));
        var stream = client.GetStream();
        await ReadPayload(stream, UploadBytes, 0x31, operation.Token);
        await WritePayload(stream, DownloadBytes, 0x52, operation.Token);
        return new(channel.Name, channel.Protocol, channel.Address, ((IPEndPoint)client.Client.RemoteEndPoint!).Port,
            channel.Port, DownloadBytes, UploadBytes);
    }

    private static async Task<Result> ServeUdp(UdpClient server, Channel channel, CancellationToken cancellation)
    {
        IPEndPoint? peer = null;
        for (var sequence = 0; sequence < UdpPackets; sequence++)
        {
            var incoming = await server.ReceiveAsync(cancellation);
            VerifyPacket(incoming.Buffer, UdpUploadPacket, sequence, 0x31);
            if (peer is not null && !peer.Equals(incoming.RemoteEndPoint)) throw new IOException("UDP peer changed.");
            peer = incoming.RemoteEndPoint;
            var reply = Packet(UdpDownloadPacket, sequence, 0x52);
            await server.SendAsync(reply, peer, cancellation);
        }
        return new(channel.Name, channel.Protocol, channel.Address, peer!.Port, channel.Port, DownloadBytes, UploadBytes);
    }

    private static async Task RunClient(string directory, bool ipv4Only, CancellationToken cancellation)
    {
        var readyPath = Path.Combine(directory, "server-ready.json");
        await WaitFile(readyPath, cancellation);
        using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(readyPath, cancellation));
        var channels = JsonSerializer.Deserialize<List<Channel>>(ready.RootElement.GetProperty("channels").GetRawText())!;
        Save(Path.Combine(directory, "client-ready.json"), new
        {
            pid = Environment.ProcessId, processPath = Environment.ProcessPath,
            startedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), channels, ipv4Only
        });
        await WaitFile(Path.Combine(directory, "start-traffic"), cancellation);
        var results = new List<Result>();
        foreach (var channel in channels)
        {
            Save(Path.Combine(directory, "client-progress.json"), new { pid = Environment.ProcessId, ipv4Only, currentChannel = channel.Name, channels = results });
            var address = IPAddress.Parse(channel.Address);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            operation.CancelAfter(TimeSpan.FromSeconds(20));
            if (channel.Protocol.StartsWith("TCP", StringComparison.Ordinal))
            {
                using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
                if (address.AddressFamily == AddressFamily.InterNetworkV6) client.Client.DualMode = false;
                await client.ConnectAsync(address, channel.Port, operation.Token);
                var localPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                var stream = client.GetStream();
                await WritePayload(stream, UploadBytes, 0x31, operation.Token);
                await ReadPayload(stream, DownloadBytes, 0x52, operation.Token);
                results.Add(new(channel.Name, channel.Protocol, channel.Address, channel.Port, localPort, UploadBytes, DownloadBytes));
            }
            else
            {
                using var client = new UdpClient(address.AddressFamily);
                if (address.AddressFamily == AddressFamily.InterNetworkV6) client.Client.DualMode = false;
                client.Client.Bind(new IPEndPoint(address, 0));
                client.Connect(new IPEndPoint(address, channel.Port));
                var localPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                for (var sequence = 0; sequence < UdpPackets; sequence++)
                {
                    await client.SendAsync(Packet(UdpUploadPacket, sequence, 0x31), operation.Token);
                    using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                    replyTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var reply = await client.ReceiveAsync(replyTimeout.Token);
                    VerifyPacket(reply.Buffer, UdpDownloadPacket, sequence, 0x52);
                }
                results.Add(new(channel.Name, channel.Protocol, channel.Address, channel.Port, localPort, UploadBytes, DownloadBytes));
            }
            Save(Path.Combine(directory, "client-progress.json"), new { pid = Environment.ProcessId, ipv4Only, currentChannel = (string?)null, channels = results });
        }
        Save(Path.Combine(directory, "client-result.json"), new { success = true, pid = Environment.ProcessId, ipv4Only, completedUtc = DateTime.UtcNow, channels = results });
    }

    private static async Task WritePayload(NetworkStream stream, int count, byte fill, CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        Array.Fill(buffer, fill);
        while (count > 0)
        {
            var length = Math.Min(buffer.Length, count);
            await stream.WriteAsync(buffer.AsMemory(0, length), cancellation);
            count -= length;
        }
    }

    private static async Task ReadPayload(NetworkStream stream, int count, byte fill, CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(count, buffer.Length)), cancellation);
            if (read == 0) throw new EndOfStreamException("TCP stream ended before the expected byte count.");
            for (var index = 0; index < read; index++)
                if (buffer[index] != fill) throw new IOException("TCP payload mismatch.");
            count -= read;
        }
    }

    private static byte[] Packet(int length, int sequence, byte fill)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, sequence);
        return bytes;
    }

    private static void VerifyPacket(byte[] bytes, int expectedLength, int sequence, byte fill)
    {
        if (bytes.Length != expectedLength || BinaryPrimitives.ReadInt32LittleEndian(bytes) != sequence)
            throw new IOException("UDP length or sequence mismatch; this run is inconclusive.");
        for (var index = sizeof(int); index < bytes.Length; index++)
            if (bytes[index] != fill) throw new IOException("UDP payload mismatch.");
    }

    private static async Task WaitFile(string path, CancellationToken cancellation)
    {
        while (!File.Exists(path)) await Task.Delay(100, cancellation);
    }

    private static void Save(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }
}
