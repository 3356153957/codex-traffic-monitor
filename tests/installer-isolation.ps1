param([Parameter(Mandatory)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$installer = [IO.Path]::GetFullPath($InstallerPath)
if ([IO.Path]::GetFileName($installer) -notlike '*-isolated-test.exe') { throw '只允许运行显式隔离构建，不能测试正式安装包。' }
$runRoot = Join-Path $projectRoot ('output\installer-isolation-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$appDirectory = Join-Path $runRoot 'installed app'
$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexTrafficMonitor.InstallerIsolationTest_is1'
$formalRegistryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexTrafficMonitor.Desktop_is1'
$group = 'Codex 流量统计（安装测试）'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) $group
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) ($group + '.lnk')
$historyDirectory = Join-Path $env:LOCALAPPDATA 'CodexTrafficMonitor'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
if (Test-Path -LiteralPath $registryKey) { throw '已有独立安装测试注册项，请先检查旧测试结果，不自动覆盖。' }
if (Test-Path -LiteralPath $startMenu) { throw '已有独立测试开始菜单目录，请先检查旧测试结果。' }
if (Test-Path -LiteralPath $desktopLink) { throw '已有独立测试桌面快捷方式，请先检查旧测试结果。' }

function Get-MonitorProcesses {
    return @(Get-Process -Name CodexTrafficMonitor -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Id = $_.Id; StartTime = try { $_.StartTime.ToUniversalTime().ToString('O') } catch { '' } } } | Sort-Object Id)
}

function Get-TaskXml {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    try {
        $scheduler = New-Object -ComObject Schedule.Service
        $scheduler.Connect()
        return [string]$scheduler.GetFolder('\').GetTask("CodexTrafficMonitor-$sid").Xml
    } catch {
        if ([uint32]$_.Exception.HResult -in @([uint32]0x80070002, [uint32]0x8004130F)) { return '' }
        throw
    }
}

function Invoke-Installer([string]$Path, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "安装生命周期返回错误：$($process.ExitCode)。详见 $runRoot" }
}

$beforeProcesses = ConvertTo-Json -InputObject (Get-MonitorProcesses) -Compress
$beforeTask = Get-TaskXml
$formalRegistryBefore = if (Test-Path -LiteralPath $formalRegistryKey) { Get-ItemProperty -LiteralPath $formalRegistryKey | Select-Object DisplayName, DisplayVersion, InstallLocation, UninstallString | ConvertTo-Json -Compress } else { '' }
$historyBefore = @{}
$dynamicHistory = [Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $historyDirectory) {
    foreach ($file in Get-ChildItem -LiteralPath $historyDirectory -File -Recurse | Where-Object { $_.Name -ne 'instance.lock' -and $_.Name -notlike '*.tmp' }) {
        $sessionSummary = Join-Path $file.DirectoryName 'session.json'
        if ((Test-Path -LiteralPath $sessionSummary) -and -not ((Get-Content -LiteralPath $sessionSummary -Raw | ConvertFrom-Json).EndedUtc)) {
            $dynamicHistory.Add($file.FullName)
            continue
        }
        try { $historyBefore[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
        catch [IO.IOException] { $dynamicHistory.Add($file.FullName) }
    }
}
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed) {
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed })
    if (-not $Passed) { throw "安装验证失败：$Name" }
}

try {
    Invoke-Installer $installer @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/LANG=zhcn', ('/DIR="' + $appDirectory + '"'), '/TASKS=desktopicon', ('/LOG="' + (Join-Path $runRoot 'install.log') + '"'))
    Check 'isolated registry created in HKCU' (Test-Path -LiteralPath $registryKey)
    Check 'application payload installed' (Test-Path -LiteralPath (Join-Path $appDirectory 'CodexTrafficMonitor.exe'))
    Check 'MIT license installed' (Test-Path -LiteralPath (Join-Path $appDirectory 'LICENSE'))
    Check 'machine verification excluded' (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'VERIFICATION.md')))
    Check 'start menu launch shortcut created' (Test-Path -LiteralPath (Join-Path $startMenu 'Codex 流量统计.lnk'))
    Check 'optional desktop shortcut created' (Test-Path -LiteralPath $desktopLink)
    # The Shell Link LinkInfo block stores this ASCII test target in its local
    # base path. Read that block directly: WScript.Shell's ANSI automation
    # marshaler can replace Chinese characters in a .lnk filename with '?'.
    $linkBytes = [IO.File]::ReadAllBytes((Join-Path $startMenu 'Codex 流量统计.lnk'))
    $linkContent = [Text.Encoding]::Latin1.GetString($linkBytes)
    Check 'shortcut points to installed executable' ($linkContent.Contains((Join-Path $appDirectory 'CodexTrafficMonitor.exe'), [StringComparison]::OrdinalIgnoreCase))
    $marker = Join-Path $appDirectory 'preserved-user-file.txt'
    Set-Content -LiteralPath $marker -Value 'installer must not delete unowned data' -Encoding utf8
    $markerHash = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
    Invoke-Installer $installer @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/LANG=zhcn', ('/DIR="' + $appDirectory + '"'), '/TASKS=desktopicon', ('/LOG="' + (Join-Path $runRoot 'upgrade.log') + '"'))
    Check 'upgrade preserves unowned files' ((Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -eq $markerHash)
    Check 'upgrade keeps one uninstaller registration' (Test-Path -LiteralPath $registryKey)
    Invoke-Installer (Join-Path $appDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $runRoot 'uninstall.log') + '"'))
    Check 'uninstall removes application executable' (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'CodexTrafficMonitor.exe')))
    Check 'uninstall removes isolated registry entry' (-not (Test-Path -LiteralPath $registryKey))
    Check 'uninstall removes desktop shortcut' (-not (Test-Path -LiteralPath $desktopLink))
    Check 'uninstall removes start menu group' (-not (Test-Path -LiteralPath $startMenu))
    Check 'uninstall preserves unowned files' ((Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -eq $markerHash)
    Check 'existing monitor processes unchanged' ((ConvertTo-Json -InputObject (Get-MonitorProcesses) -Compress) -eq $beforeProcesses)
    Check 'real startup task unchanged' ((Get-TaskXml) -eq $beforeTask)
    $formalRegistryAfter = if (Test-Path -LiteralPath $formalRegistryKey) { Get-ItemProperty -LiteralPath $formalRegistryKey | Select-Object DisplayName, DisplayVersion, InstallLocation, UninstallString | ConvertTo-Json -Compress } else { '' }
    Check 'formal product registration unchanged' ($formalRegistryAfter -eq $formalRegistryBefore)
    $historyUnchanged = $true
    foreach ($path in $historyBefore.Keys) {
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $historyBefore[$path]) { $historyUnchanged = $false; break }
    }
    Check 'stable user data files preserved without modification' $historyUnchanged
    Check 'active user data files still exist' (@($dynamicHistory | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -eq 0)
    $report = [ordered]@{ Success = $true; InstallerFileName = [IO.Path]::GetFileName($installer); Checks = $checks; ExistingMonitorProcesses = ($beforeProcesses | ConvertFrom-Json); StableDataFilesChecked = $historyBefore.Count; ActiveDataFilesPreserved = $dynamicHistory.Count; MaintenanceIntegrationTested = $false; Scope = 'Isolated installer packaging lifecycle; maintenance commands and application launch disabled at compile time.' }
} catch {
    $report = [ordered]@{ Success = $false; Error = $_.Exception.Message; Checks = $checks }
    throw
} finally {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 8
}
