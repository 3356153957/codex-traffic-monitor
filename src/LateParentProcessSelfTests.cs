using System.IO;

namespace CodexTraffic;

internal static class LateParentProcessSelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + name);
            checks.Add(name);
        }
        DateTime at = DateTime.UtcNow.AddSeconds(-2);
        string claude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe");
        const int parent = 1_910_000_000, child = parent + 1, grandchild = parent + 2;
        var late = new ProcessCatalog();
        late.RecordStarted(child, parent, "tool.exe", at.AddMilliseconds(20), @"C:\fixture\tool.exe");
        late.RecordStopped(child, at.AddMilliseconds(90));
        Check(late.GetTracked(child, true, at.AddMilliseconds(50)) == null,
            "An exited tool remains unassigned before its parent's identity is known");
        late.RecordStarted(parent, 0, "claude.exe", at, claude);
        Check(late.GetTracked(child, true, at.AddMilliseconds(50))?.Application == TrackedApplication.Claude,
            "A late parent start repairs its already-exited child's historical application ownership");
        Check(late.Get(child) == null && !late.IsTracked(child, true) &&
            late.GetTracked(child, false, at.AddMilliseconds(50)) == null &&
            late.GetTracked(child, true, at.AddMilliseconds(10)) == null &&
            late.GetTracked(child, true, at.AddMilliseconds(91)) == null,
            "Repair neither revives a process nor extends its lifetime or overrides the children toggle");

        var chain = new ProcessCatalog();
        chain.RecordStopped(grandchild, at.AddMilliseconds(80));
        chain.RecordStarted(grandchild, child, "worker.exe", at.AddMilliseconds(30), @"C:\fixture\worker.exe");
        chain.RecordStopped(child, at.AddMilliseconds(90));
        chain.RecordStarted(child, parent, "tool.exe", at.AddMilliseconds(20), @"C:\fixture\tool.exe");
        chain.RecordStopped(parent, at.AddMilliseconds(100));
        chain.RecordStarted(parent, 0, "claude.exe", at, claude);
        Check(chain.GetTracked(grandchild, true, at.AddMilliseconds(50))?.Application == TrackedApplication.Claude && chain.List(true).Count == 0,
            "Late starts reconcile a fully retired multi-level family after stop-before-start delivery");

        var reused = new ProcessCatalog();
        reused.RecordStarted(child, parent, "tool.exe", at.AddMilliseconds(20), @"C:\fixture\tool.exe");
        reused.RecordStopped(child, at.AddMilliseconds(90));
        reused.RecordStarted(parent, 0, "claude.exe", at.AddMilliseconds(100), claude);
        Check(reused.GetTracked(child, true, at.AddMilliseconds(50)) == null,
            "A reused parent PID born after the retired child cannot claim it");

        var gap = new ProcessCatalog();
        gap.RecordStopped(parent, at.AddMilliseconds(10));
        gap.RecordStarted(child, parent, "tool.exe", at.AddMilliseconds(20), @"C:\fixture\tool.exe");
        gap.RecordStopped(child, at.AddMilliseconds(90));
        gap.RecordStarted(parent, 0, "claude.exe", at, claude);
        Check(gap.GetTracked(child, true, at.AddMilliseconds(50)) == null,
            "A retired parent whose lifetime ended before child birth cannot establish ownership");

        var unknown = new ProcessCatalog();
        unknown.RecordStarted(child, parent, "tool.exe", DateTime.MinValue, @"C:\fixture\tool.exe");
        unknown.RecordStopped(child, at.AddMilliseconds(90));
        unknown.RecordStarted(parent, 0, "claude.exe", at, claude);
        Check(unknown.GetTracked(child, true, at.AddMilliseconds(50)) == null,
            "A retired child's unknown birth time is insufficient for retrospective parent repair");

        var nested = new ProcessCatalog();
        nested.RecordStarted(child, parent, "codex.exe", at.AddMilliseconds(20), @"C:\fixture\codex.exe");
        nested.RecordStopped(child, at.AddMilliseconds(90));
        nested.RecordStarted(parent, 0, "claude.exe", at, claude);
        Check(nested.GetTracked(child, true, at.AddMilliseconds(50))?.Application == TrackedApplication.Codex,
            "Late ancestor repair preserves a nested application's own core identity");
        return checks;
    }
}
