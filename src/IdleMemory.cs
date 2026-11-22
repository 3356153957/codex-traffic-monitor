using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace CodexTraffic;

/// <summary>
/// Returns memory that a hidden, non-capturing instance no longer touches.
/// Building the dashboard leaves large-object-heap fragmentation and native
/// pages that stay resident while the app sits in the tray; one compacting
/// collection plus a working-set trim hands them back to Windows. Pages fault
/// back in transparently when the window is shown again.
/// </summary>
internal static class IdleMemory
{
    internal static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        try
        {
            using var self = Process.GetCurrentProcess();
            SetProcessWorkingSetSizeEx(self.Handle, -1, -1, 0);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nint minimum, nint maximum, uint flags);
}
