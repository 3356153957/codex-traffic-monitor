using System.IO;

namespace CodexTraffic;

public static class ApplicationProcessSelfTests
{
    public static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        var at = DateTime.UtcNow.AddSeconds(-30);
        const int firstPid = 1_800_000_000; // Synthetic IDs never address a real process.
        var nextPid = firstPid;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var desktop = @"C:\Program Files\WindowsApps\Claude_4.16336.0.0_x64__pzs8sxrjxfjjc\app\claude.exe";
        var cli = Path.Combine(home, @".local\bin\claude.exe");
        var npmCli = Path.Combine(home, @"AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe");
        var nodeScript = Path.Combine(home, @"AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js");

        (ProcessCatalog Catalog, int Pid) One(string name, string path, string? commandLine = null)
        {
            var catalog = new ProcessCatalog();
            var pid = nextPid++;
            catalog.RecordStarted(pid, 0, name, at, path, commandLine);
            return (catalog, pid);
        }
        bool IsClaude(string name, string path, string? commandLine = null)
        {
            var item = One(name, path, commandLine);
            return item.Catalog.GetTracked(item.Pid, false, at.AddSeconds(1))?.Application == TrackedApplication.Claude;
        }
        bool Untracked(string name, string path, string? commandLine = null)
        {
            var item = One(name, path, commandLine);
            return !item.Catalog.IsTracked(item.Pid, true);
        }

        Check(new ProcessInfo(1, 0, "legacy", "", "core", at).Application == TrackedApplication.Codex,
            "Existing ProcessInfo constructors retain Codex ownership by default");
        Check(IsClaude("claude.exe", desktop), "Claude desktop MSIX is identified by its exact package publisher suffix");
        Check(IsClaude("Claude.exe", Path.Combine(local, @"AnthropicClaude\Claude.exe")) &&
            IsClaude("Claude.exe", Path.Combine(local, @"AnthropicClaude\app-0.7.9\Claude.exe")),
            "Claude desktop direct and versioned legacy installation paths are recognized");
        Check(IsClaude("claude.exe", cli), "Claude Code native per-user launcher is recognized");
        Check(IsClaude("claude.exe", npmCli) && IsClaude("claude.exe", @"D:\npm\node_modules\@anthropic-ai\claude-code-win32-arm64\claude.exe"),
            "Claude Code npm binaries include scoped main and Windows platform packages");
        Check(Untracked("claude.exe", @"D:\UnrelatedApp\claude.exe") && Untracked("claude.exe", ""),
            "An unrelated same-name Claude executable or an unavailable path is not guessed as Claude");
        Check(Untracked("claude.exe", desktop.Replace("pzs8sxrjxfjjc", "anotherpublisher")) &&
            Untracked("claude.exe", Path.Combine(local, @"AnthropicClaudeBackup\claude.exe")),
            "Similar package names, wrong publishers, and neighboring folders do not establish Claude ownership");
        Check(Untracked("ChatGPT.exe", @"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0_x64__test\app\ChatGPT.exe"),
            "Standalone ChatGPT remains excluded from Codex accounting");
        Check(IsClaude("claude-ssh-proxy.exe", desktop.Replace("claude.exe", @"resources\claude-ssh-proxy.exe")) &&
            Untracked("claude-ssh-proxy.exe", @"D:\Other\claude-ssh-proxy.exe"),
            "Claude built-in helpers require the trusted desktop installation path");
        Check(IsClaude("node.exe", @"C:\Program Files\nodejs\node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" \"" + nodeScript + "\" --version"),
            "Older Claude npm Node installations are identified from the exact first CLI script argument");
        Check(IsClaude("node.exe", @"C:\node\node.exe", "node -- \"" + nodeScript.Replace('\\', '/') + "\""),
            "Claude npm script identification handles quoted paths, forward slashes, and the runtime option terminator");
        Check(Untracked("node.exe", @"C:\node\node.exe", "node C:\\Other\\script.js \"" + nodeScript + "\"") &&
            Untracked("node.exe", @"C:\node\node.exe", "node -e \"" + nodeScript + "\"") &&
            Untracked("node.exe", @"C:\node\node.exe", "node --require \"" + nodeScript + "\""),
            "Claude paths in data arguments, inline code, or preload options do not label unrelated Node programs");
        Check(Untracked("node.exe", @"C:\node\node.exe", "node .\\node_modules\\@anthropic-ai\\claude-code\\cli.js") &&
            Untracked("node.exe", @"C:\node\node.exe", "node \"" + nodeScript.Replace("claude-code", "claude-code-copy") + "\""),
            "Relative and similarly named npm scripts do not establish unverified ownership");

        var tree = new ProcessCatalog();
        var codex = nextPid++; var claude = nextPid++; var claudeTool = nextPid++; var nestedCodex = nextPid++; var codexTool = nextPid++;
        tree.RecordStarted(codex, 0, "codex.exe", at, @"D:\Portable\codex.exe");
        tree.RecordStarted(claude, codex, "claude.exe", at.AddSeconds(1), cli);
        tree.RecordStarted(claudeTool, claude, "python.exe", at.AddSeconds(2), @"C:\Python\python.exe");
        tree.RecordStarted(nestedCodex, claudeTool, "codex.exe", at.AddSeconds(3), @"D:\Portable\codex.exe");
        tree.RecordStarted(codexTool, nestedCodex, "curl.exe", at.AddSeconds(4), @"C:\Windows\System32\curl.exe");
        Check(tree.GetTracked(claude, true, at.AddSeconds(5))?.Application == TrackedApplication.Claude &&
            tree.GetTracked(claudeTool, true, at.AddSeconds(5))?.Application == TrackedApplication.Claude,
            "Claude launched by Codex becomes the application owner of its own tools");
        Check(tree.GetTracked(nestedCodex, true, at.AddSeconds(5))?.Application == TrackedApplication.Codex &&
            tree.GetTracked(codexTool, true, at.AddSeconds(5))?.Application == TrackedApplication.Codex,
            "Codex launched by Claude becomes the application owner of its own tools");
        Check(tree.List(true).Count == 5 && tree.List(true).Select(x => x.Pid).Distinct().Count() == 5 &&
            tree.List(true).Count(x => x.Application == TrackedApplication.Claude) == 2,
            "Nested tracked applications have one owner per PID and are never double counted");
        Check(tree.List(false).Count == 3 && !tree.IsTracked(claudeTool, false) &&
            tree.Get(claudeTool)?.FriendlyKind == "Claude 派生工具",
            "The derived-tool option excludes only tools and retains application-specific descriptions");
        tree.RecordStopped(claude, at.AddSeconds(6));
        Check(tree.GetTracked(claudeTool, true, at.AddSeconds(7))?.Application == TrackedApplication.Claude,
            "A tool retains its birth-time application owner after the Claude parent exits");

        var reused = new ProcessCatalog();
        var reusedPid = nextPid++;
        reused.RecordStarted(reusedPid, 0, "claude.exe", at, cli);
        reused.RecordStopped(reusedPid, at.AddSeconds(4));
        reused.RecordStarted(reusedPid, 0, "codex.exe", at.AddSeconds(5), @"D:\Portable\codex.exe");
        Check(reused.GetTracked(reusedPid, true, at.AddSeconds(3))?.Application == TrackedApplication.Claude &&
            reused.GetTracked(reusedPid, true, at.AddSeconds(6))?.Application == TrackedApplication.Codex,
            "Delayed network events resolve the correct application across PID reuse");
        reused.RecordStopped(reusedPid, at.AddSeconds(4));
        Check(reused.GetTracked(reusedPid, true, at.AddSeconds(6))?.Application == TrackedApplication.Codex,
            "A delayed old stop cannot remove a newer reused-PID application");
        reused.RecordStarted(reusedPid, 0, "claude.exe", at.AddSeconds(1), cli);
        Check(reused.Get(reusedPid)?.Application == TrackedApplication.Codex,
            "A delayed old start cannot replace a newer reused-PID application");

        var reusedParent = new ProcessCatalog();
        var parent = nextPid++; var oldTool = nextPid++;
        reusedParent.RecordStarted(parent, 0, "notepad.exe", at, @"C:\Windows\notepad.exe");
        reusedParent.RecordStarted(oldTool, parent, "curl.exe", at.AddSeconds(1), @"C:\Windows\System32\curl.exe");
        reusedParent.RecordStopped(parent, at.AddSeconds(2));
        reusedParent.RecordStarted(parent, 0, "claude.exe", at.AddSeconds(3), cli);
        Check(!reusedParent.IsTracked(oldTool, true), "A new Claude process cannot adopt an older unrelated child of the reused parent PID");
        var lateParent = nextPid++; var lateChild = nextPid++;
        reusedParent.RecordStarted(lateParent, 0, "claude.exe", at.AddSeconds(5), cli);
        reusedParent.RecordStopped(lateParent, at.AddSeconds(9));
        reusedParent.RecordStarted(lateChild, lateParent, "git.exe", at.AddSeconds(7), @"C:\Git\git.exe");
        Check(reusedParent.GetTracked(lateChild, true, at.AddSeconds(8))?.Application == TrackedApplication.Claude,
            "A late child-start event can resolve a recently exited Claude parent within its bounded lifetime");

        var excluded = new ProcessCatalog(); var ownChild = nextPid++; var ownGrandchild = nextPid++;
        excluded.RecordStarted(ownChild, Environment.ProcessId, "claude.exe", at, cli);
        excluded.RecordStarted(ownGrandchild, ownChild, "node.exe", at.AddSeconds(1), @"C:\node\node.exe", "node \"" + nodeScript + "\"");
        Check(!excluded.IsTracked(ownChild, true) && !excluded.IsTracked(ownGrandchild, true),
            "The monitor's own tools stay excluded even when they launch a recognized Claude CLI");
        return checks;
    }
}
