using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexTraffic;

public static class ApplicationLedgerSelfTests
{
    public static List<string> Run()
    {
        var checks = new List<string>();
        string data = Path.Combine(Path.GetTempPath(), "CodexApplicationLedgerTests", Guid.NewGuid().ToString("N"));
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name + " (fixtures: " + data + ")");
            checks.Add(name);
        }
        var ledger = new TrafficLedger(); DateTime now = DateTime.UtcNow;
        void Add(long bytes, bool upload, TrackedApplication app) => ledger.Add(now, 101, "node.exe", "C:\\fixture\\node.exe", "child",
            "TCP", "127.0.0.1", 51000, "127.0.0.1", 7897, bytes, upload, app);
        Add(13, true, TrackedApplication.Codex); Add(17, false, TrackedApplication.Codex);
        Add(19, true, TrackedApplication.Claude); Add(23, false, TrackedApplication.Claude);
        Check(ledger.Totals(DashboardApplicationScope.Total) == (32L, 40L) && ledger.Totals(DashboardApplicationScope.Codex) == (13L, 17L) &&
            ledger.Totals(DashboardApplicationScope.Claude) == (19L, 23L),
            "Ledger directional totals separately track Codex and Claude and reconcile combined traffic");
        Check(ledger.UploadBytes == ledger.CodexUploadBytes + ledger.ClaudeUploadBytes &&
            ledger.DownloadBytes == ledger.CodexDownloadBytes + ledger.ClaudeDownloadBytes,
            "Ledger combined properties are exactly the sum of application properties");
        Check(ledger.Targets().Count == 2 && ledger.Processes().Count == 2 && ledger.Pending().Count == 2 &&
            ledger.Targets(DashboardApplicationScope.Codex).Single().UploadBytes == 13 &&
            ledger.Targets(DashboardApplicationScope.Claude).Single().DownloadBytes == 23,
            "The same PID, process name, path and socket remain separate across applications");
        Check(ledger.Processes(DashboardApplicationScope.Codex).Single().Application == TrackedApplication.Codex &&
            ledger.Processes(DashboardApplicationScope.Claude).Single().UploadBytes == 19,
            "Process filters retain each application's child-process ownership and bytes");
        var codexFlow = ledger.Pending().Single(f => f.Application == TrackedApplication.Codex);
        var claudeFlow = ledger.Pending().Single(f => f.Application == TrackedApplication.Claude);
        ledger.Annotate(codexFlow, new ProxyHint("codex.test", "1.1.1.1", 443, "Auto → Proxy", "fixture", now, "Proxy", "", "Vless"));
        ledger.Annotate(claudeFlow, new ProxyHint("claude.test", "1.1.1.2", 443, "DIRECT", "fixture", now, "DIRECT", "", "Direct"));
        Check(ledger.Targets(DashboardApplicationScope.Codex).Single().TrafficKind == TrafficKind.Proxy &&
            ledger.Targets(DashboardApplicationScope.Claude).Single().TrafficKind == TrafficKind.Direct &&
            codexFlow.ProxyReportedHost == "codex.test" && claudeFlow.ProxyReportedHost == "claude.test",
            "Proxy annotations do not bleed between different applications on the same socket identity");
        var mutableTarget = ledger.Targets(DashboardApplicationScope.Claude).Single(); mutableTarget.UploadBytes = 9999;
        var mutableProcess = ledger.Processes(DashboardApplicationScope.Codex).Single(); mutableProcess.DownloadBytes = 9999;
        Check(ledger.Targets(DashboardApplicationScope.Claude).Single().UploadBytes == 19 &&
            ledger.Processes(DashboardApplicationScope.Codex).Single().DownloadBytes == 17,
            "Filtered ledger row mutations cannot alter its accounting state");

        string sessionPath;
        using (var store = new SessionStore(data, children: true, loopback: true))
        {
            sessionPath = store.DirectoryPath;
            store.Append(ledger.Pending());
            store.Update(32, 40, 0, codexUpload: 13, codexDownload: 17, claudeUpload: 19, claudeDownload: 23);
            var persisted = JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(Path.Combine(sessionPath, "session.json")))!;
            Check(persisted.AccountingVersion == 2 && persisted.UploadBytes == 32 && persisted.DownloadBytes == 40 &&
                persisted.CodexUploadBytes == 13 && persisted.CodexDownloadBytes == 17 && persisted.ClaudeUploadBytes == 19 && persisted.ClaudeDownloadBytes == 23,
                "SessionStore writes version-two canonical per-application counters and their exact combined total");
            using var flowReader = new StreamReader(new FileStream(store.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var flowLines = flowReader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var savedFlows = flowLines.Select(l => JsonSerializer.Deserialize<FlowRecord>(l)!).ToList();
            Check(savedFlows.Count == 2 && savedFlows.Single(f => f.Application == TrackedApplication.Codex).UploadBytes == 13 &&
                savedFlows.Single(f => f.Application == TrackedApplication.Claude).DownloadBytes == 23 &&
                flowLines.All(l => { using var document = JsonDocument.Parse(l); return document.RootElement.TryGetProperty("Application", out _); }),
                "Persisted JSONL intervals carry explicit application attribution with independent directional bytes");
            string before = File.ReadAllText(Path.Combine(sessionPath, "session.json"));
            bool rejected = false;
            try { store.Update(32, 40, 0, codexUpload: 14, codexDownload: 17, claudeUpload: 19, claudeDownload: 23); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(sessionPath, "session.json")) == before && store.Record.CodexUploadBytes == 13,
                "Inconsistent application counters are rejected before modifying the canonical record or file");
            rejected = false;
            try { store.Update(32, 40, 0, codexUpload: -1, codexDownload: 17, claudeUpload: 33, claudeDownload: 23); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(sessionPath, "session.json")) == before,
                "Negative application counters cannot be persisted even if their sum matches the total");
            store.Update(32, 40, 0, ended: true, claudeUpload: 19, claudeDownload: 23);
            Check(store.Record.CodexUploadBytes == 13 && store.Record.CodexDownloadBytes == 17 && store.Record.EndedUtc != null,
                "SessionStore infers omitted Codex counters from combined minus Claude counters when ending a recording");
        }
        var sessionRow = SessionStore.History(data).Single(r => r.Path == sessionPath);
        Check(sessionRow.AccountingVersion == 2 && sessionRow.CodexUploadBytes == 13 && sessionRow.ClaudeDownloadBytes == 23 && sessionRow.UploadBytes == 32,
            "Session-history rows preserve version-two per-application canonical attribution");
        var history = new HistoryAnalytics(data); history.Refresh();
        Check(history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Codex).UploadBytes == 13 &&
            history.GetSnapshot(DashboardRange.All, DashboardTrafficScope.All, DashboardApplicationScope.Claude).DownloadBytes == 23 &&
            history.GetSnapshot(DashboardRange.All).UploadBytes == 32,
            "New session JSONL and version-two canonical summaries reach application dashboard totals without double counting");

        string legacyDirectory = Path.Combine(data, "legacy", "sessions", "old"); Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "session.json"), JsonSerializer.Serialize(new SessionRecord
        { Id = "old", StartedUtc = now, UpdatedUtc = now, EndedUtc = now, Status = "completed", UploadBytes = 7, DownloadBytes = 11 }));
        File.WriteAllText(Path.Combine(legacyDirectory, "flows.jsonl"), "", new UTF8Encoding(false));
        var legacyRow = SessionStore.History(Path.Combine(data, "legacy")).Single();
        Check(legacyRow.CodexUploadBytes == 7 && legacyRow.CodexDownloadBytes == 11 && legacyRow.ClaudeUploadBytes == 0 && legacyRow.ClaudeDownloadBytes == 0,
            "Pre-Claude session summaries remain Codex-only in session-history rows");

        string invalidHistory = Path.Combine(data, "invalid-history");
        void SummaryFixture(string id, object record)
        {
            string directory = Path.Combine(invalidHistory, "sessions", id); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(record));
        }
        SessionRecord ValidSummary() => new()
        {
            AccountingVersion = 2, StartedUtc = now, UpdatedUtc = now, EndedUtc = now, Status = "completed",
            UploadBytes = 32, DownloadBytes = 40, CodexUploadBytes = 13, ClaudeUploadBytes = 19,
            CodexDownloadBytes = 17, ClaudeDownloadBytes = 23
        };
        var badUpload = ValidSummary(); badUpload.CodexUploadBytes++;
        var badDownload = ValidSummary(); badDownload.ClaudeDownloadBytes++;
        SummaryFixture("bad-upload", badUpload); SummaryFixture("bad-download", badDownload);
        SummaryFixture("missing-application-fields", new
        { AccountingVersion = 2, StartedUtc = now, UpdatedUtc = now, UploadBytes = 32, DownloadBytes = 40 });
        Check(SessionStore.History(invalidHistory).Count == 0,
            "Session-history rejects mismatched directional totals and missing version-two application counters instead of guessing ownership");
        var negativeCodexUp = ValidSummary(); negativeCodexUp.CodexUploadBytes = -1; negativeCodexUp.ClaudeUploadBytes = 33;
        var negativeClaudeUp = ValidSummary(); negativeClaudeUp.ClaudeUploadBytes = -1; negativeClaudeUp.CodexUploadBytes = 33;
        var negativeCodexDown = ValidSummary(); negativeCodexDown.CodexDownloadBytes = -1; negativeCodexDown.ClaudeDownloadBytes = 41;
        var negativeClaudeDown = ValidSummary(); negativeClaudeDown.ClaudeDownloadBytes = -1; negativeClaudeDown.CodexDownloadBytes = 41;
        SummaryFixture("negative-codex-up", negativeCodexUp); SummaryFixture("negative-claude-up", negativeClaudeUp);
        SummaryFixture("negative-codex-down", negativeCodexDown); SummaryFixture("negative-claude-down", negativeClaudeDown);
        Check(SessionStore.History(invalidHistory).Count == 0,
            "Session-history rejects each negative application counter even when the directional application sums match");
        var oversizedSum = ValidSummary(); oversizedSum.CodexUploadBytes = long.MaxValue; oversizedSum.ClaudeUploadBytes = long.MaxValue;
        var negativeTotal = ValidSummary(); negativeTotal.UploadBytes = -32;
        var noStart = ValidSummary(); noStart.StartedUtc = default;
        SummaryFixture("overflowing-sum", oversizedSum); SummaryFixture("negative-total", negativeTotal); SummaryFixture("missing-start", noStart);
        string overflowDirectory = Path.Combine(invalidHistory, "sessions", "overflowing-value"); Directory.CreateDirectory(overflowDirectory);
        File.WriteAllText(Path.Combine(overflowDirectory, "session.json"), JsonSerializer.Serialize(ValidSummary()).Replace("\"UploadBytes\":32", "\"UploadBytes\":9223372036854775808", StringComparison.Ordinal));
        Check(SessionStore.History(invalidHistory).Count == 0,
            "Corrupt session totals, dates, out-of-range numeric values and overflowing application sums are skipped without throwing");
        SummaryFixture("valid-v2", ValidSummary());
        SummaryFixture("valid-zero-v2", new SessionRecord { AccountingVersion = 2, StartedUtc = now, UpdatedUtc = now });
        SummaryFixture("valid-legacy", new SessionRecord { StartedUtc = now, UpdatedUtc = now, UploadBytes = 7, DownloadBytes = 11, ClaudeUploadBytes = 999 });
        var validHistory = SessionStore.History(invalidHistory);
        Check(validHistory.Count == 3 && validHistory.Single(r => Path.GetFileName(r.Path) == "valid-v2").ClaudeDownloadBytes == 23 &&
            validHistory.Single(r => Path.GetFileName(r.Path) == "valid-zero-v2").UploadBytes == 0 &&
            validHistory.Single(r => Path.GetFileName(r.Path) == "valid-legacy").CodexUploadBytes == 7 &&
            validHistory.Single(r => Path.GetFileName(r.Path) == "valid-legacy").ClaudeUploadBytes == 0,
            "Skipping corrupt session summaries preserves valid split totals, zero-byte sessions and legacy Codex ownership");

        string csvPath = Path.Combine(data, "application.csv");
        var export = ledger.Targets();
        export.Single(t => t.Application == TrackedApplication.Claude).Process = "=UNTRUSTED()";
        export.Single(t => t.Application == TrackedApplication.Codex).Hostname = "@UNTRUSTED";
        SessionStore.WriteCsv(csvPath, export);
        string csv = File.ReadAllText(csvPath); string[] lines = File.ReadAllLines(csvPath);
        Check(lines[0].EndsWith(",Application", StringComparison.Ordinal) && lines.Length == 3 &&
            lines.Any(l => l.EndsWith("\"Codex\"", StringComparison.Ordinal)) && lines.Any(l => l.EndsWith("\"Claude\"", StringComparison.Ordinal)),
            "CSV exports include the application column and distinguish both applications");
        Check(csv.Contains("\"'=UNTRUSTED()\"") && csv.Contains("\"'@UNTRUSTED\""),
            "Application-aware CSV exports retain spreadsheet-formula escaping for untrusted text");
        ledger.CommitPending();
        Check(ledger.Pending().Count == 0 && ledger.Totals(DashboardApplicationScope.Total) == (32L, 40L),
            "Committing persisted intervals clears the pending batch without resetting application lifetime totals");
        var compatibility = new TrafficLedger();
        compatibility.Add(now, 1, "codex.exe", "C:\\fixture\\codex.exe", "core", "TCP", "127.0.0.1", 51001, "127.0.0.1", 7897, 5, true);
        Check(compatibility.Totals(DashboardApplicationScope.Codex) == (5L, 0L) && compatibility.Totals(DashboardApplicationScope.Claude) == (0L, 0L),
            "Existing ledger callers that omit application continue to record Codex traffic");
        return checks;
    }
}
