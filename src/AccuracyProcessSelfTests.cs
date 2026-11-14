using System.Collections;
using System.IO;
using System.Reflection;

namespace CodexTraffic;

internal static class AccuracyProcessSelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        const int basePid = 1_900_000_000;
        int nextPid = basePid;
        DateTime at = DateTime.UtcNow.AddSeconds(-2);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string native = Path.Combine(home, @".local\bin\claude.exe");
        string desktop = @"C:\Program Files\WindowsApps\Claude_4.16336.0.0_x64__pzs8sxrjxfjjc\app\claude.exe";
        string nodeScript = Path.Combine(home, @"AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js");

        (ProcessCatalog Catalog, int Pid) Start(string name, string commandLine, string? path = null)
        {
            var catalog = new ProcessCatalog();
            int pid = nextPid++;
            // Same shape as MonitorEngine's ETW callback: an exited PID cannot
            // answer the native query and no executablePath was supplied.
            catalog.RecordStarted(pid, 0, name, at, path, commandLine);
            return (catalog, pid);
        }
        bool IsClaude(string name, string commandLine)
        {
            var value = Start(name, commandLine);
            return value.Catalog.GetTracked(value.Pid, false, at.AddMilliseconds(1))?.Application == TrackedApplication.Claude;
        }
        bool Untracked(string commandLine)
        {
            var value = Start("claude.exe", commandLine);
            return !value.Catalog.IsTracked(value.Pid, true);
        }

        Check(IsClaude("claude.exe", "\"" + native + "\" --version"),
            "A fast exited Claude CLI is recognized from the real ETW callback's absolute executable token");
        Check(IsClaude("CLAUDE.EXE", "  \"" + desktop + "\" --private-fixture=ignored") &&
            IsClaude("claude.exe", "\"" + desktop.Replace('\\', '/') + "\" --version"),
            "Quoted Claude desktop paths with spaces, case differences and forward slashes retain trusted package ownership");
        Check(Untracked(@".\.local\bin\claude.exe --version") && Untracked("claude.exe \"" + native + "\""),
            "A relative or bare executable token cannot borrow a trusted Claude path from later arguments");
        Check(Untracked("\"C:\\Windows\\System32\\cmd.exe\" /c \"" + native + "\"") &&
            Untracked("\"" + Path.ChangeExtension(native, ".com") + "\" \"" + native + "\""),
            "A different first executable filename cannot establish Claude ownership from arguments");
        Check(Untracked(@"D:\UnrelatedApp\claude.exe --version") &&
            Untracked("\"" + desktop.Replace("pzs8sxrjxfjjc", "otherpublisher") + "\" --version"),
            "An absolute same-name executable still must pass the existing trusted installation whitelist");
        Check(Untracked("\"" + native + " --version") && Untracked("\"" + native + "\" " + new string('x', 65536)),
            "Malformed quotes and oversized command lines cannot provide fallback image paths");
        var providedPath = Start("claude.exe", "\"" + native + "\" --version", @"D:\Other\claude.exe");
        Check(!providedPath.Catalog.IsTracked(providedPath.Pid, false),
            "Command-line fallback cannot override an available executable image path");
        Check(IsClaude("node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" \"" + nodeScript + "\" --version"),
            "Absolute executable fallback preserves existing exact Node CLI script recognition");

        const string privateArgument = "ARGUMENT_MUST_NOT_SURVIVE_PROCESS_IDENTIFICATION";
        var privacy = Start("claude.exe", "\"" + native + "\" --private-fixture=" + privateArgument);
        bool noLiveArguments = !ContainsString(privacy.Catalog, privateArgument, new HashSet<object>(ReferenceEqualityComparer.Instance));
        privacy.Catalog.RecordStopped(privacy.Pid, at.AddMilliseconds(100));
        Check(privacy.Catalog.GetTracked(privacy.Pid, false, at.AddMilliseconds(50))?.Path == Path.GetFullPath(native) &&
            noLiveArguments && !ContainsString(privacy.Catalog, privateArgument, new HashSet<object>(ReferenceEqualityComparer.Instance)),
            "Process identification retains the executable path without retaining command-line arguments in live or retired state");

        var late = new ProcessCatalog();
        int latePid = nextPid++;
        late.RecordStopped(latePid, at.AddMilliseconds(100));
        late.Refresh(); // A native refresh must not immediately forget the stop.
        late.RecordStarted(latePid, 0, "claude.exe", at, executablePath: null, commandLine: "\"" + native + "\" --version");
        Check(late.Get(latePid) == null && !late.IsTracked(latePid, true) &&
            late.GetTracked(latePid, false, at.AddMilliseconds(50))?.Application == TrackedApplication.Claude &&
            late.GetTracked(latePid, false, at.AddMilliseconds(101)) == null,
            "A stop delivered before its start recovers only the exited lifetime and never revives the PID");
        late.Refresh();
        Check(late.Get(latePid) == null && late.GetTracked(latePid, false, at.AddMilliseconds(50))?.Application == TrackedApplication.Claude &&
            late.GetTracked(latePid, false, at.AddMilliseconds(101)) == null,
            "Native refresh preserves late-start historical resolution without making an exited PID current");
        late.RecordStarted(latePid, 0, "codex.exe", at.AddMilliseconds(200), @"D:\Portable\codex.exe");
        late.RecordStopped(latePid, at.AddMilliseconds(100));
        Check(late.GetTracked(latePid, false, at.AddMilliseconds(50))?.Application == TrackedApplication.Claude &&
            late.GetTracked(latePid, false, at.AddMilliseconds(150)) == null &&
            late.GetTracked(latePid, false, at.AddMilliseconds(250))?.Application == TrackedApplication.Codex,
            "A newer reused PID starts normally while delayed old stop events preserve the historical gap and current owner");

        var descendants = new ProcessCatalog();
        int parent = nextPid++, child = nextPid++;
        descendants.RecordStarted(parent, 0, "claude.exe", at, native);
        descendants.RecordStopped(parent, at.AddMilliseconds(100));
        descendants.RecordStopped(child, at.AddMilliseconds(90));
        descendants.RecordStarted(child, parent, "curl.exe", at.AddMilliseconds(20), @"C:\Windows\System32\curl.exe");
        Check(descendants.Get(child) == null &&
            descendants.GetTracked(child, true, at.AddMilliseconds(50))?.Application == TrackedApplication.Claude &&
            descendants.GetTracked(child, false, at.AddMilliseconds(50)) == null &&
            descendants.GetTracked(child, true, at.AddMilliseconds(95)) == null,
            "A child whose stop precedes its start inherits a recently exited parent only within its bounded lifetime");

        var bounded = new ProcessCatalog();
        for (int i = 0; i < 4200; i++) bounded.RecordStopped(basePid + 1000 + i, at);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var markers = (IDictionary)typeof(ProcessCatalog).GetField("_stoppedVersions", flags)!.GetValue(bounded)!;
        var order = (ICollection)typeof(ProcessCatalog).GetField("_stopOrder", flags)!.GetValue(bounded)!;
        Check(markers.Count <= 4096 && order.Count <= 4096,
            "Recent stop markers and their eviction queue remain bounded during process churn");
        return checks;
    }

    private static bool ContainsString(object? value, string searched, HashSet<object> seen)
    {
        if (value is null) return false;
        if (value is string text) return text.Contains(searched, StringComparison.Ordinal);
        if (!seen.Add(value)) return false;
        if (value is IDictionary dictionary)
        {
            foreach (var item in dictionary.Values) if (ContainsString(item, searched, seen)) return true;
            return false;
        }
        if (value is IEnumerable items)
        {
            foreach (var item in items) if (ContainsString(item, searched, seen)) return true;
            return false;
        }
        if (value.GetType().Namespace != typeof(ProcessCatalog).Namespace) return false;
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (ContainsString(field.GetValue(value), searched, seen)) return true;
        return false;
    }
}
