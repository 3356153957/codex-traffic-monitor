using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

public static class UpdateCheckerSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        string Release(string version, string? url = null, bool draft = false, bool prerelease = false, bool installer = true, string state = "uploaded")
        {
            string tag = "v" + version;
            string name = "CodexTrafficMonitor-Setup-" + version + "-win-x64.exe";
            return JsonSerializer.Serialize(new
            {
                tag_name = tag, draft, prerelease,
                assets = installer ? new[] { new { name, state, browser_download_url = url ?? UpdateChecker.RepositoryUrl + "/releases/download/" + tag + "/" + name } }
                    : Array.Empty<object>()
            });
        }
        var available = UpdateChecker.ParseRelease(Release("1.10.0"), "1.9.0");
        Check(available.Status == UpdateCheckStatus.Available && available.LatestVersion == "1.10.0" && available.CurrentVersion == "1.9.0" &&
            available.DownloadUrl == UpdateChecker.RepositoryUrl + "/releases/download/v1.10.0/CodexTrafficMonitor-Setup-1.10.0-win-x64.exe",
            "Update checker compares numeric version components and exposes the exact official installer URL");
        Check(UpdateChecker.ParseRelease(Release("1.4.0"), "1.4.0").Status == UpdateCheckStatus.UpToDate &&
            UpdateChecker.ParseRelease(Release("1.4.0"), "1.5.0").Status == UpdateCheckStatus.UpToDate,
            "Equal or newer installed versions never offer a downgrade");
        Check(UpdateChecker.ParseRelease(Release("1.4.1"), "1.4.0+commit").Status == UpdateCheckStatus.Available &&
            UpdateChecker.ParseRelease(Release("1.4.0"), "1.4.0.0").Status == UpdateCheckStatus.UpToDate,
            "Build metadata and equivalent fourth version components preserve correct version comparison");
        Check(UpdateChecker.ParseRelease(Release("2.0.0", draft: true), "1.4.0").Status != UpdateCheckStatus.Available &&
            UpdateChecker.ParseRelease(Release("2.0.0", prerelease: true), "1.4.0").Status != UpdateCheckStatus.Available,
            "Draft and prerelease releases never appear as available stable updates");
        Check(new[] { "{bad-json}", "null", "[]", "{}", "{\"tag_name\":\"v2.0.0\",\"assets\":[]}" }
            .All(json => UpdateChecker.ParseRelease(json, "1.4.0").Status == UpdateCheckStatus.Error),
            "Malformed JSON and missing stable-release metadata produce a recoverable error");
        Check(UpdateChecker.ParseRelease(Release("2.0.0"), "unknown").Status == UpdateCheckStatus.Error &&
            UpdateChecker.ParseRelease(Release("2.0.0-beta"), "1.4.0").Status == UpdateCheckStatus.Error,
            "Unrecognized current and remote version strings cannot offer an update");
        Check(UpdateChecker.ParseRelease(Release("2.0.0", installer: false), "1.4.0").Status == UpdateCheckStatus.Error &&
            UpdateChecker.ParseRelease(Release("2.0.0", state: "new"), "1.4.0").Status == UpdateCheckStatus.Error,
            "A newer release without an uploaded Windows x64 installer reports an error without a download link");
        string correct = UpdateChecker.RepositoryUrl + "/releases/download/v2.0.0/CodexTrafficMonitor-Setup-2.0.0-win-x64.exe";
        var unsafeUrls = new[]
        {
            correct.Replace("https:", "http:"), correct.Replace("github.com/", "github.com.evil.example/"),
            correct.Replace("github.com/", "github.com:444/"), correct.Replace("github.com/", "user@github.com/"),
            correct.Replace("3356153957/", "someone-else/"), correct.Replace("codex-traffic-monitor/", "another-repo/"),
            correct.Replace("/v2.0.0/", "/v9.0.0/"), correct + "?source=elsewhere", correct + "#fragment",
            correct.Replace("-win-x64.exe", "-win-x64.zip"), "https://example.com/installer.exe"
        };
        Check(unsafeUrls.All(url =>
        {
            var result = UpdateChecker.ParseRelease(Release("2.0.0", url), "1.4.0");
            return result.Status == UpdateCheckStatus.Error && result.DownloadUrl.Length == 0;
        }), "Update checker rejects non-HTTPS, spoofed hosts, credentials, wrong repositories, mismatched assets, ports, queries and fragments");
        Check(UpdateChecker.ParseRelease(Release("2.0.0").Replace("Setup-2.0.0-win-x64.exe\"", "Setup-2.0.0-win-arm64.exe\""), "1.4.0").Status == UpdateCheckStatus.Error,
            "A different architecture or asset name is never selected as the Windows x64 installer");
        Check(UpdateChecker.ParseRelease(new string(' ', 1024 * 1024 + 1), "1.4.0").Status == UpdateCheckStatus.Error,
            "Release parsing refuses payloads above its response limit");

        UpdateCheckResult Request(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response, CancellationToken cancellationToken = default)
        {
            using var client = new HttpClient(new StubHandler(response));
            return UpdateChecker.CheckAsync(client, "1.4.0", cancellationToken).GetAwaiter().GetResult();
        }
        HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        int requests = 0; bool expectedRequest = false;
        var fetched = Request((request, _) =>
        {
            requests++;
            expectedRequest = request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == UpdateChecker.LatestReleaseEndpoint &&
                request.Headers.UserAgent.ToString().Contains("CodexTrafficMonitor/1.4.0", StringComparison.Ordinal) &&
                request.Headers.Accept.Any(a => a.MediaType == "application/vnd.github+json") && request.Headers.Authorization == null && request.Content == null;
            return Task.FromResult(JsonResponse(Release("2.0.0")));
        });
        Check(fetched.Status == UpdateCheckStatus.Available && requests == 1 && expectedRequest,
            "Manual update checks issue one anonymous GET to this repository's GitHub latest-release endpoint");
        Check(new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect }
            .All(status => Request((_, _) => Task.FromResult(new HttpResponseMessage(status))).Status == UpdateCheckStatus.Error),
            "Rate limits, missing releases, server errors and redirects produce recoverable update errors");
        Check(Request((_, _) => Task.FromResult(JsonResponse("not-json"))).Status == UpdateCheckStatus.Error,
            "A successful HTTP status with invalid JSON is handled as an update error");
        Check(Request((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture failure"))).Status == UpdateCheckStatus.Error,
            "Network failures return an update result instead of escaping into the UI");
        var oversizedDeclared = Request((_, _) =>
        {
            var response = JsonResponse("{}"); response.Content.Headers.ContentLength = 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        Check(oversizedDeclared.Status == UpdateCheckStatus.Error && oversizedDeclared.Message.Contains("过大"),
            "An oversized declared response is rejected before reading the release body");
        var oversizedStream = Request((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(new byte[1024 * 1024 + 1])) }));
        Check(oversizedStream.Status == UpdateCheckStatus.Error && oversizedStream.Message.Contains("过大"),
            "Chunked responses without content length are bounded while streaming");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var canceledResult = Request((_, token) => Task.FromCanceled<HttpResponseMessage>(token), canceled.Token);
        Check(canceledResult.Status == UpdateCheckStatus.Error && canceledResult.Message.Contains("取消") && canceledResult.DownloadUrl.Length == 0,
            "Caller cancellation ends update checks with an explicit canceled result and no download action");
        return checks;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
