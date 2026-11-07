using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

public sealed class SessionStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly FileStream _flowStream;
    public string DirectoryPath { get; }
    public string LogFile => Path.Combine(DirectoryPath, "flows.jsonl");
    public SessionRecord Record { get; }

    public SessionStore(string dataDirectory, bool children, bool loopback)
    {
        var now = DateTime.UtcNow;
        string id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        DirectoryPath = Path.Combine(dataDirectory, "sessions", id);
        Directory.CreateDirectory(DirectoryPath);
        Record = new SessionRecord { AccountingVersion = 2, Id = id, StartedUtc = now, UpdatedUtc = now, IncludeChildren = children, IncludeLoopback = loopback };
        _flowStream = new FileStream(LogFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        _flowStream.Position = _flowStream.Length;
        try { Update(0, 0, 0); }
        catch { _flowStream.Dispose(); throw; }
    }

    public void Append(IEnumerable<FlowRecord> rows)
    {
        string block = string.Join("\n", rows.Select(row => JsonSerializer.Serialize(row)));
        if (block.Length == 0) return;
        byte[] bytes = Encoding.UTF8.GetBytes(block + "\n");
        long checkpoint = _flowStream.Position;
        try { _flowStream.Write(bytes); _flowStream.Flush(true); }
        catch
        {
            try { _flowStream.SetLength(checkpoint); _flowStream.Position = checkpoint; } catch { }
            throw;
        }
    }

    public void Update(long up, long down, long lost, string? error = null, bool ended = false,
        long? codexUpload = null, long? codexDownload = null, long claudeUpload = 0, long claudeDownload = 0)
    {
        long codexUp = codexUpload ?? checked(up - claudeUpload);
        long codexDown = codexDownload ?? checked(down - claudeDownload);
        if (codexUp < 0 || codexDown < 0 || claudeUpload < 0 || claudeDownload < 0 ||
            checked(codexUp + claudeUpload) != up || checked(codexDown + claudeDownload) != down)
            throw new InvalidOperationException("应用流量分项与合计不一致，未写入会话摘要。");
        Record.UpdatedUtc = DateTime.UtcNow;
        Record.CodexUploadBytes = codexUp; Record.CodexDownloadBytes = codexDown;
        Record.ClaudeUploadBytes = claudeUpload; Record.ClaudeDownloadBytes = claudeDownload;
        Record.UploadBytes = up;
        Record.DownloadBytes = down;
        Record.EventsLost = lost;
        if (error != null) { Record.Error = error; Record.Status = "error"; }
        if (ended) { Record.EndedUtc = Record.UpdatedUtc; if (Record.Status != "error") Record.Status = "completed"; }
        string file = Path.Combine(DirectoryPath, "session.json");
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Record, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, file, true);
    }

    public static List<SessionRow> History(string dataDirectory, string? excludedId = null)
    {
        string directory = Path.Combine(dataDirectory, "sessions");
        if (!Directory.Exists(directory)) return new();
        var result = new List<SessionRow>();
        foreach (var path in Directory.EnumerateDirectories(directory).OrderByDescending(x => x).Take(100))
        {
            if (Path.GetFileName(path) == excludedId) continue;
            try
            {
                var r = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(Path.Combine(path, "session.json")));
                if (r == null || r.StartedUtc == default || r.UploadBytes < 0 || r.DownloadBytes < 0) continue;
                // Use the same validity rules as the dashboard history index.
                // Corrupt version-two counters must not show a combined total in
                // one view and different application totals in another. Decimal
                // comparison also rejects sums that exceed the Int64 range.
                if (r.AccountingVersion >= 2 && (r.CodexUploadBytes < 0 || r.CodexDownloadBytes < 0 ||
                    r.ClaudeUploadBytes < 0 || r.ClaudeDownloadBytes < 0 ||
                    (decimal)r.CodexUploadBytes + r.ClaudeUploadBytes != r.UploadBytes ||
                    (decimal)r.CodexDownloadBytes + r.ClaudeDownloadBytes != r.DownloadBytes)) continue;
                var duration = (r.EndedUtc ?? r.UpdatedUtc) - r.StartedUtc;
                result.Add(new SessionRow { AccountingVersion = r.AccountingVersion,
                    CodexUploadBytes = r.AccountingVersion >= 2 ? r.CodexUploadBytes : r.UploadBytes,
                    CodexDownloadBytes = r.AccountingVersion >= 2 ? r.CodexDownloadBytes : r.DownloadBytes,
                    ClaudeUploadBytes = r.AccountingVersion >= 2 ? r.ClaudeUploadBytes : 0,
                    ClaudeDownloadBytes = r.AccountingVersion >= 2 ? r.ClaudeDownloadBytes : 0,
                    StartedAt = r.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Duration = $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}" + (r.Status == "recording" ? "（未正常结束）" : r.Status == "error" ? "（采集异常）" : ""), UploadBytes = r.UploadBytes, DownloadBytes = r.DownloadBytes, Path = path });
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public static void WriteCsv(string path, IEnumerable<TargetRow> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Process,PID,Protocol,LocalAddress,LocalPort,RemoteAddress,RemotePort,Route,UploadBytes,DownloadBytes,LastSeenLocal,DomainHint,DomainSource,ProxyReportedDestination,ProxyChain,ProxyNode,Region,RegionSource,TrafficKind,ProxyType,TrafficKindSource,Application");
        foreach (var row in rows)
        {
            TrafficClassifier.Apply(row);
            writer.WriteLine(string.Join(",", new[] { SafeCsv(row.Process), row.Pid.ToString(CultureInfo.InvariantCulture), SafeCsv(row.Protocol), SafeCsv(row.LocalAddress), row.LocalPort.ToString(CultureInfo.InvariantCulture), SafeCsv(row.Address), row.Port.ToString(CultureInfo.InvariantCulture), SafeCsv(row.Route), row.UploadBytes.ToString(CultureInfo.InvariantCulture), row.DownloadBytes.ToString(CultureInfo.InvariantCulture), SafeCsv(row.LastSeen.ToString("O", CultureInfo.InvariantCulture)), SafeCsv(row.Hostname), SafeCsv(row.DomainSource), SafeCsv(row.ProxyDestination), SafeCsv(row.ProxyChain), SafeCsv(row.ProxyNode), SafeCsv(row.Region), SafeCsv(row.RegionSource), SafeCsv(row.TrafficKind.ToString()), SafeCsv(row.ProxyType), SafeCsv(row.TrafficKindSource), SafeCsv(row.Application.ToString()) }));
        }
    }

    private static string SafeCsv(string value)
    {
        // Excel treats formula prefixes specially even inside quoted CSV cells.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public void Dispose() => _flowStream.Dispose();
}
