using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexTraffic;

public enum UpdateCheckStatus { UpToDate, Available, Error }

public sealed record UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string Message { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
}

// Manual, anonymous version requests only. No telemetry, traffic records,
// credentials, downloads, or installers are sent or run by the checker.
public static class UpdateChecker
{
    public const string RepositoryUrl = "https://github.com/3356153957/codex-traffic-monitor";
    internal const string LatestReleaseEndpoint = "https://api.github.com/repos/3356153957/codex-traffic-monitor/releases/latest";
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    public static string CurrentVersion => (typeof(UpdateChecker).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];

    public static Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        => CheckAsync(Client, CurrentVersion, cancellationToken);

    internal static async Task<UpdateCheckResult> CheckAsync(HttpClient client, string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.UserAgent.ParseAdd("CodexTrafficMonitor/" + currentVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Error(currentVersion, "GitHub 暂时限制了更新请求，请稍后重试。");
            if (!response.IsSuccessStatusCode)
                return Error(currentVersion, "更新服务暂时不可用（HTTP " + (int)response.StatusCode + "），请稍后重试。");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return Error(currentVersion, "更新服务返回的内容过大，已停止检查。");
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaximumResponseBytes)
                    return Error(currentVersion, "更新服务返回的内容过大，已停止检查。");
                bytes.Write(buffer, 0, read);
            }
            return ParseRelease(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length), currentVersion);
        }
        catch (OperationCanceledException)
        { return Error(currentVersion, cancellationToken.IsCancellationRequested ? "更新检查已取消。" : "检查更新超时，请检查网络后重试。"); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or FormatException)
        { return Error(currentVersion, "无法连接 GitHub 更新服务，请检查网络或代理后重试。"); }
    }

    internal static UpdateCheckResult ParseRelease(string json, string currentVersion)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes)
                return Error(currentVersion, "更新服务返回的内容过大，已停止检查。");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var release = document.RootElement;
            if (release.ValueKind != JsonValueKind.Object ||
                !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
                return Error(currentVersion, "更新服务未返回正式发布版本，请稍后重试。");
            string tag = String(release, "tag_name");
            if (!TryVersion(currentVersion, out var current) || !TryVersion(tag, out var latest))
                return Error(currentVersion, "无法识别版本号，请在 GitHub 发行页核对。");
            string latestLabel = tag.StartsWith('v') ? tag[1..] : tag;
            if (latest <= current)
                return new() { Status = UpdateCheckStatus.UpToDate, CurrentVersion = currentVersion, LatestVersion = latestLabel,
                    Message = latest == current ? "当前已是最新版本（" + currentVersion + "）。" : "当前版本 " + currentVersion + " 高于已发布版本 " + latestLabel + "。" };

            string expectedName = "CodexTrafficMonitor-Setup-" + latestLabel + "-win-x64.exe";
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object || String(asset, "name") != expectedName || String(asset, "state") != "uploaded") continue;
                    string url = String(asset, "browser_download_url");
                    if (!IsOfficialDownload(url, tag, expectedName)) continue;
                    return new() { Status = UpdateCheckStatus.Available, CurrentVersion = currentVersion, LatestVersion = latestLabel,
                        DownloadUrl = url, Message = "发现新版本 " + latestLabel + "。打开安装包下载后，运行安装向导即可更新，历史记录保留。" };
                }
            }
            return Error(currentVersion, "已发布版本 " + latestLabel + " 暂无可用的 Windows x64 安装包，请稍后重试。");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        { return Error(currentVersion, "更新服务返回的格式异常，请稍后重试。"); }
    }

    public static void OpenDownload(UpdateCheckResult result)
    {
        if (result.Status != UpdateCheckStatus.Available || !TryVersion(result.LatestVersion, out _))
            throw new InvalidOperationException("请先成功检查更新。");
        string name = "CodexTrafficMonitor-Setup-" + result.LatestVersion + "-win-x64.exe";
        bool valid = IsOfficialDownload(result.DownloadUrl, "v" + result.LatestVersion, name) || IsOfficialDownload(result.DownloadUrl, result.LatestVersion, name);
        if (!valid) throw new InvalidOperationException("安装包下载地址不属于本项目，未打开。");
        // Browser handles the download. An explicit user action is still needed to
        // run the installer and accept any required Windows administrator prompt.
        Process.Start(new ProcessStartInfo(result.DownloadUrl) { UseShellExecute = true });
    }

    internal static bool IsOfficialDownload(string url, string tag, string filename)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath == "/3356153957/codex-traffic-monitor/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(filename);

    private static string String(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool TryVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        value = value.Split('+')[0];
        if (!Regex.IsMatch(value, @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))?$", RegexOptions.CultureInvariant)) return false;
        if (!Version.TryParse(value.TrimStart('v'), out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }
    private static UpdateCheckResult Error(string currentVersion, string message)
        => new() { Status = UpdateCheckStatus.Error, CurrentVersion = currentVersion, Message = message };
}
