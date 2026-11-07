[CmdletBinding()]
param(
    [string]$RunDirectory,
    [string]$DotNetPath,
    [Alias('MonitorExe')][string]$MonitorPath,
    [int]$CaptureWaitSeconds = 180,
    [ValidateRange(20, 120)][int]$ProbeSeconds = 35,
    [switch]$FixtureOnly,
    [switch]$PrepareOnly,
    [switch]$StartCapture,
    [switch]$IPv4Only
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $RunDirectory) { $RunDirectory = Join-Path $PSScriptRoot ('runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
if ((Test-Path -LiteralPath $RunDirectory) -and -not (Test-Path -LiteralPath (Join-Path $RunDirectory 'prepared.json'))) { throw "Use a new run directory: $RunDirectory" }
if (Test-Path -LiteralPath (Join-Path $RunDirectory 'diagnostic-plan.json')) { throw "This diagnostic directory has already been used: $RunDirectory" }
New-Item -ItemType Directory -Path $RunDirectory -Force | Out-Null
if (-not $DotNetPath) { $DotNetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $DotNetPath)) { $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source }
if (-not $MonitorPath) { $MonitorPath = Join-Path $projectRoot 'dist\CodexTrafficMonitor.exe' }
$MonitorPath = [IO.Path]::GetFullPath($MonitorPath)

function Save-Json($Value, [string]$Path) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding UTF8
}
function Quote-Argument([string]$Value) {
    if ($Value.Contains('"')) { throw 'A path must not contain a quote.' }
    return '"' + $Value + '"'
}
function Wait-File([string]$Path, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (Test-Path -LiteralPath $Path)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for $Path" }
        Start-Sleep -Milliseconds 100
    }
}
function Check-Process($Process, [string]$Role) {
    $Process.Refresh()
    if ($Process.HasExited -and $Process.ExitCode -ne 0) { throw "$Role failed with exit code $($Process.ExitCode). See logs under $RunDirectory" }
}

$server = $null
$client = $null
$captureProcess = $null
try {
    # A deliberately named codex.exe would also be seen by another active monitor.
    # Refuse to touch an existing monitor; do not stop somebody else's recording.
    if (-not $PrepareOnly -and @(Get-Process -Name CodexTrafficMonitor -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'An existing CodexTrafficMonitor process is open. Close that instance before this isolated diagnostic.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $isAdmin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
    if ($StartCapture -and -not $PrepareOnly -and -not $FixtureOnly -and -not $isAdmin) {
        throw '-StartCapture requires this entire script to run from administrator PowerShell. The script never requests UAC itself. Elevating only the monitor may leave fixture sockets restricted.'
    }
    $fixtureOutput = Join-Path $RunDirectory 'fixture'
    & $DotNetPath build (Join-Path $PSScriptRoot 'network-fixture\NetworkFixture.csproj') -c Release --nologo --output $fixtureOutput
    if ($LASTEXITCODE -ne 0) { throw 'Network fixture build failed.' }
    $dll = Join-Path $fixtureOutput 'codex.dll'
    $exe = Join-Path $fixtureOutput 'codex.exe'
    if ($PrepareOnly) {
        $prepared = [ordered]@{ RunDirectory = $RunDirectory; FixtureExe = $exe; DotNetPath = $DotNetPath; MonitorPath = $MonitorPath; ProbeSeconds = $ProbeSeconds; IPv4Only = [bool]$IPv4Only; Note = 'Build only. No fixture was started and no UAC or ETW capture was attempted. Reuse this directory without -PrepareOnly to run.' }
        Save-Json $prepared (Join-Path $RunDirectory 'prepared.json')
        Write-Output ($prepared | ConvertTo-Json -Depth 4)
        return
    }
    $serverArguments = @((Quote-Argument $dll), '--server', (Quote-Argument $RunDirectory))
    if ($IPv4Only) { $serverArguments += '--ipv4-only' }
    $server = Start-Process -FilePath $DotNetPath -ArgumentList $serverArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $RunDirectory 'server-stdout.log') -RedirectStandardError (Join-Path $RunDirectory 'server-stderr.log')
    Wait-File (Join-Path $RunDirectory 'server-ready.json')
    if ($FixtureOnly) {
        # No misleading process name is needed for the unprivileged socket check.
        $clientArguments = @((Quote-Argument $dll), '--client', (Quote-Argument $RunDirectory))
        if ($IPv4Only) { $clientArguments += '--ipv4-only' }
        $client = Start-Process -FilePath $DotNetPath -ArgumentList $clientArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $RunDirectory 'client-stdout.log') -RedirectStandardError (Join-Path $RunDirectory 'client-stderr.log')
    } else {
        if (-not (Test-Path -LiteralPath $MonitorPath)) { throw "Monitor executable not found: $MonitorPath" }
        $clientArguments = @('--client', (Quote-Argument $RunDirectory))
        if ($IPv4Only) { $clientArguments += '--ipv4-only' }
        $client = Start-Process -FilePath $exe -ArgumentList $clientArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $RunDirectory 'client-stdout.log') -RedirectStandardError (Join-Path $RunDirectory 'client-stderr.log')
    }
    Wait-File (Join-Path $RunDirectory 'client-ready.json')
    $clientReady = Get-Content -LiteralPath (Join-Path $RunDirectory 'client-ready.json') -Raw | ConvertFrom-Json
    $captureOutput = Join-Path $RunDirectory 'capture.json'
    $probeData = Join-Path $RunDirectory 'probe-data'
    $captureArguments = '--capture-probe ' + (Quote-Argument $captureOutput) + ' --seconds ' + $ProbeSeconds
    $plan = [ordered]@{
        RunDirectory = $RunDirectory; ClientPid = $clientReady.pid; ClientStartedUtc = $clientReady.startedUtc
        MonitorPath = $MonitorPath; CaptureArguments = $captureArguments; CaptureOutput = $captureOutput
        FixtureOnly = [bool]$FixtureOnly
        StartCapture = [bool]$StartCapture
        IPv4Only = [bool]$IPv4Only
        Note = 'With -StartCapture the already elevated script starts the monitor without another UAC. Otherwise separately start the monitor using the recorded arguments. This script never requests elevation.'
    }
    Save-Json $plan (Join-Path $RunDirectory 'diagnostic-plan.json')
    if (-not $FixtureOnly) {
        if ($StartCapture) {
            $captureProcess = Start-Process -FilePath $MonitorPath -ArgumentList $captureArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $RunDirectory 'capture-stdout.log') -RedirectStandardError (Join-Path $RunDirectory 'capture-stderr.log')
            Write-Output "Isolated capture started: $MonitorPath $captureArguments"
        } else {
            Write-Output "Ready. Separately launch with administrator rights: $MonitorPath $captureArguments"
        }
        $deadline = [DateTime]::UtcNow.AddSeconds($CaptureWaitSeconds)
        $sessionFile = $null
        while (-not $sessionFile) {
            Check-Process $client 'Client'
            Check-Process $server 'Server'
            if (Test-Path -LiteralPath $captureOutput) { throw 'Capture ended before the fixture could start; inspect capture.json.' }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for the separately elevated capture. No test traffic was sent.' }
            $sessionsDirectory = Join-Path $probeData 'sessions'
            if (Test-Path -LiteralPath $sessionsDirectory) {
                $sessionFile = @(Get-ChildItem -LiteralPath $sessionsDirectory -Directory | ForEach-Object { Join-Path $_.FullName 'session.json' } | Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1
            }
            if (-not $sessionFile) { Start-Sleep -Milliseconds 100 }
        }
        # Session metadata is created just before EnableKernelProvider; allow it to settle.
        Start-Sleep -Seconds 2
    }
    New-Item -ItemType File -Path (Join-Path $RunDirectory 'start-traffic') | Out-Null
    Wait-File (Join-Path $RunDirectory 'client-result.json') 90
    $clientResult = Get-Content -LiteralPath (Join-Path $RunDirectory 'client-result.json') -Raw | ConvertFrom-Json
    if (-not $clientResult.success) { throw 'Socket client failed; inspect client-result.json.' }
    Wait-File (Join-Path $RunDirectory 'server-result.json') 30
    $serverResult = Get-Content -LiteralPath (Join-Path $RunDirectory 'server-result.json') -Raw | ConvertFrom-Json
    if (-not $clientResult.success -or -not $serverResult.success) { throw 'Socket fixture failed; inspect client-result.json and server-result.json.' }
    foreach ($channel in $clientResult.channels) {
        if ($channel.UploadBytes -ne 262144 -or $channel.DownloadBytes -ne 131072) { throw "Unexpected fixture byte count: $($channel.Name)" }
    }
    if ($FixtureOnly) {
        $report = [ordered]@{ success = $true; scope = 'Unprivileged socket fixture only; no ETW capture was attempted.'; IPv4Only = [bool]$IPv4Only; channels = $clientResult.channels; runDirectory = $RunDirectory }
    } else {
        Wait-File $captureOutput $CaptureWaitSeconds
        if ($null -ne $captureProcess) { $captureProcess.WaitForExit(5000) | Out-Null }
        $capture = Get-Content -LiteralPath $captureOutput -Raw | ConvertFrom-Json
        if (-not $capture.PSObject.Properties['snapshot']) { throw 'Capture produced no snapshot; inspect capture.json.' }
        $flowFile = Join-Path (Split-Path -Parent $sessionFile) 'flows.jsonl'
        $clientStart = ([DateTime]$clientReady.startedUtc).ToUniversalTime()
        $clientEnd = ([DateTime]$clientResult.completedUtc).ToUniversalTime()
        $flows = @(Get-Content -LiteralPath $flowFile | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.Pid -eq $clientReady.pid -and ([DateTime]$_.TimestampUtc).ToUniversalTime() -ge $clientStart -and ([DateTime]$_.TimestampUtc).ToUniversalTime() -le $clientEnd })
        $checks = @()
        foreach ($channel in $clientResult.channels) {
            $matching = @($flows | Where-Object { $_.Protocol -eq $channel.Protocol -and $_.RemoteAddress -eq $channel.Address -and $_.RemotePort -eq $channel.RemotePort -and $_.LocalPort -eq $channel.LocalPort })
            $actualUp = [long]0
            $actualDown = [long]0
            foreach ($row in $matching) { $actualUp += $row.UploadBytes; $actualDown += $row.DownloadBytes }
            $checks += [pscustomobject]@{
                Channel = $channel.Name; RemoteAddress = $channel.Address; RemotePort = $channel.RemotePort; LocalPort = $channel.LocalPort
                ExpectedUploadBytes = $channel.UploadBytes; ActualUploadBytes = $actualUp
                ExpectedDownloadBytes = $channel.DownloadBytes; ActualDownloadBytes = $actualDown
                DirectionReversed = ($actualUp -eq $channel.DownloadBytes -and $actualDown -eq $channel.UploadBytes)
                Passed = ($actualUp -eq $channel.UploadBytes -and $actualDown -eq $channel.DownloadBytes)
            }
        }
        $unmatched = @($flows | Where-Object {
            $flow = $_
            @($clientResult.channels | Where-Object { $_.Protocol -eq $flow.Protocol -and $_.Address -eq $flow.RemoteAddress -and $_.RemotePort -eq $flow.RemotePort -and $_.LocalPort -eq $flow.LocalPort }).Count -eq 0
        })
        $report = [ordered]@{
            success = (@($checks | Where-Object { -not $_.Passed }).Count -eq 0 -and $unmatched.Count -eq 0 -and $capture.snapshot.EventsLost -eq 0)
            scope = 'Real production ETW capture; isolated data directory; results filtered to the fixture PID.'
            IPv4Only = [bool]$IPv4Only
            clientPid = $clientReady.pid; checks = $checks; unmatchedClientFlows = $unmatched
            eventsLost = $capture.snapshot.EventsLost; clientFlows = $flows; flowFile = $flowFile; runDirectory = $RunDirectory
        }
    }
    Save-Json $report (Join-Path $RunDirectory 'network-check.json')
    Write-Output ($report | ConvertTo-Json -Depth 12)
    if (-not $report.success) { throw 'Network accounting check failed; inspect network-check.json.' }
} catch {
    Save-Json ([ordered]@{ success = $false; error = $_.Exception.ToString(); fixtureOnly = [bool]$FixtureOnly; IPv4Only = [bool]$IPv4Only; runDirectory = $RunDirectory; occurredUtc = [DateTime]::UtcNow.ToString('O') }) (Join-Path $RunDirectory 'diagnostic-error.json')
    throw
} finally {
    foreach ($process in @($client, $server)) {
        if ($null -eq $process) { continue }
        try { $process.Refresh(); if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null } } catch { }
        $process.Dispose()
    }
    if ($null -ne $captureProcess) {
        # Let our bounded probe finish and close its own ETW session, including on a fixture error.
        try { $captureProcess.WaitForExit(($ProbeSeconds + 10) * 1000) | Out-Null } catch { }
        $captureProcess.Dispose()
    }
}
