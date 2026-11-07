[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$InnoCompiler,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$destination = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot 'output\release' }
$payload = Join-Path $destination 'payload'
$project = [xml](Get-Content -LiteralPath (Join-Path $taskRoot 'src\CodexTrafficMonitor.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if (-not $InnoCompiler) { $InnoCompiler = & (Join-Path $taskRoot 'tools\ensure-inno-setup.ps1') }
& (Join-Path $taskRoot 'build.ps1') -OutputDirectory $payload -SkipTests:$SkipTests
& (Join-Path $taskRoot 'build-installer.ps1') -PayloadDirectory $payload -OutputDirectory $destination -InnoCompiler $InnoCompiler
$portable = Join-Path $destination "CodexTrafficMonitor-$version-win-x64-portable.zip"
$names = @('CodexTrafficMonitor.exe', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
Compress-Archive -LiteralPath @($names | ForEach-Object { Join-Path $payload $_ }) -DestinationPath $portable -Force
$installer = Join-Path $destination "CodexTrafficMonitor-Setup-$version-win-x64.exe"
$checksums = @($installer, $portable) | ForEach-Object {
    $digest = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    $digest + '  ' + [IO.Path]::GetFileName($_)
}
$checksums | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding ascii
Write-Host "发布文件已生成：$destination"
