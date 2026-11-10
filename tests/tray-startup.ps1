param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
if (Get-Process -Name CodexTrafficMonitor -ErrorAction SilentlyContinue) {
    throw 'Exit the running monitor normally before this test.'
}
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class TrayStartupWindows {
    public delegate bool Callback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    public static IntPtr[] Visible(int pid) {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, p) => { uint owner; GetWindowThreadProcessId(hwnd, out owner);
            if (owner == pid && IsWindowVisible(hwnd)) windows.Add(hwnd); return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
}
'@
$checks = [Collections.Generic.List[string]]::new()
$monitor = $null
$state = Join-Path ([IO.Path]::GetTempPath()) ('traffic-tray-test-' + [guid]::NewGuid().ToString('N') + '.json')
function Check($Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $checks.Add($Name)
}
function Stop-TestMonitor {
    $helper = Start-Process -FilePath $Executable -ArgumentList @('--maintenance', 'prepare', '--install-path', ('"' + (Split-Path $Executable) + '"'), '--state', ('"' + $state + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($helper.ExitCode -ne 0) { throw "Graceful exit failed; state: $state" }
}
try {
    $monitor = Start-Process -FilePath $Executable -ArgumentList '--minimized' -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 100
        Check (!$monitor.HasExited -and [TrayStartupWindows]::Visible($monitor.Id).Count -eq 0) 'Background launch has no visible window'
    }
    $checks.Clear()
    $checks.Add('Background launch stayed alive with zero visible windows for five seconds')
    $duplicate = Start-Process -FilePath $Executable -ArgumentList '--minimized' -WindowStyle Hidden -Wait -PassThru
    Check ($duplicate.ExitCode -eq 0 -and [TrayStartupWindows]::Visible($monitor.Id).Count -eq 0) 'Duplicate background launch stays hidden'
    for ($cycle = 0; $cycle -lt 2; $cycle++) {
        # A regular second launch uses the same restore channel as the tray.
        $restore = Start-Process -FilePath $Executable -WindowStyle Hidden -Wait -PassThru
        Start-Sleep -Seconds 2
        $visible = [TrayStartupWindows]::Visible($monitor.Id)
        Check ($restore.ExitCode -eq 0 -and $visible.Count -eq 1) "Restore cycle $cycle shows one window"
        [void][TrayStartupWindows]::PostMessage($visible[0], 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Seconds 2
        Check (!$monitor.HasExited -and ![TrayStartupWindows]::IsWindowVisible($visible[0])) "Close cycle $cycle hides the main window (tray notification allowed)"
    }
    Stop-TestMonitor
    Check ($monitor.WaitForExit(10000)) 'Restored monitor exits gracefully'
    $monitor = Start-Process -FilePath $Executable -ArgumentList '--minimized' -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    Stop-TestMonitor
    Check ($monitor.WaitForExit(10000)) 'Never-shown monitor exits gracefully'
    @{ Success = $true; Checks = $checks; Executable = $Executable } | ConvertTo-Json | Set-Content -LiteralPath $ReportPath
} catch {
    @{ Success = $false; Checks = $checks; Error = $_.Exception.ToString(); Executable = $Executable } | ConvertTo-Json | Set-Content -LiteralPath $ReportPath
    throw
} finally {
    if ($monitor -and !$monitor.HasExited) { Stop-TestMonitor }
}
