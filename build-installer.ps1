param(
    [string]$PayloadDirectory,
    [string]$OutputDirectory,
    [Alias('CompilerPath')][string]$InnoCompiler,
    [switch]$IsolatedTest
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$payload = if ($PayloadDirectory) { [IO.Path]::GetFullPath($PayloadDirectory) } else { Join-Path $taskRoot 'dist' }
$destination = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot 'output\release-installer' }
$project = [xml](Get-Content -LiteralPath (Join-Path $taskRoot 'src\CodexTrafficMonitor.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "不支持的安装版本：$version" }
$executable = Join-Path $payload 'CodexTrafficMonitor.exe'
foreach ($name in @('CodexTrafficMonitor.exe', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "安装源缺少 $name，请先运行 build.ps1。" }
}
$payloadVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion -replace '\+.*$', ''
if ($payloadVersion -ne $version) { throw "安装源版本 $payloadVersion 与源码版本 $version 不一致，请先重新发布。" }
if (-not $InnoCompiler) {
    $portableCompiler = Join-Path $taskRoot '.tools\inno-setup-7.1.1\compiler\ISCC.exe'
    if (Test-Path -LiteralPath $portableCompiler) { $InnoCompiler = $portableCompiler }
    else {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $InnoCompiler = $command.Source }
        else {
            foreach ($programRoot in @(${env:ProgramFiles}, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
                if (-not $programRoot) { continue }
                foreach ($relative in @('Inno Setup 7\ISCC.exe', 'Programs\Inno Setup 7\ISCC.exe')) {
                    $candidate = Join-Path $programRoot $relative
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $InnoCompiler = $candidate; break }
                }
                if ($InnoCompiler) { break }
            }
        }
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw '请先运行 tools/ensure-inno-setup.ps1，或通过 -InnoCompiler 指定官方 ISCC.exe。https://jrsoftware.org/isdl.php'
}
$compilerVersion = (& $InnoCompiler --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^7\.' -or [version]$compilerVersion -lt [version]'7.1.1') {
    throw '安装脚本要求 Inno Setup 7.1.1 或更新版本。请运行 tools/ensure-inno-setup.ps1。'
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$arguments = @('/Qp', "/DAppVersion=$version", "/DPayloadDirectory=$payload", "/DInstallerOutput=$destination", "/DIsolatedTest=$([int][bool]$IsolatedTest)", (Join-Path $taskRoot 'installer\CodexTrafficMonitor.iss'))
& $InnoCompiler @arguments
if ($LASTEXITCODE -ne 0) { throw "安装包编译失败：$LASTEXITCODE" }
$suffix = if ($IsolatedTest) { '-isolated-test' } else { '' }
$installer = Join-Path $destination "CodexTrafficMonitor-Setup-$version-win-x64$suffix.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw '编译器未生成预期安装文件。' }
$metadata = [ordered]@{
    Version = $version
    FileName = [IO.Path]::GetFileName($installer)
    Bytes = (Get-Item -LiteralPath $installer).Length
    SHA256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    PayloadSHA256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    IsolatedTest = [bool]$IsolatedTest
    PerUser = $true
    PreservesHistory = $true
    CompilerVersion = $compilerVersion
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination "installer-metadata$suffix.json") -Encoding utf8
$metadata | ConvertTo-Json
