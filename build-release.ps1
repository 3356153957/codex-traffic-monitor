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
$project = [xml](Get-Content -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'src\CodexTrafficMonitor.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if (-not $InnoCompiler) { $InnoCompiler = & (Join-Path $taskRoot 'tools\ensure-inno-setup.ps1') }
& (Join-Path $taskRoot 'build.ps1') -OutputDirectory $payload -SkipTests:$SkipTests
& (Join-Path $taskRoot 'build-installer.ps1') -PayloadDirectory $payload -OutputDirectory $destination -InnoCompiler $InnoCompiler
$portable = Join-Path $destination "CodexTrafficMonitor-$version-win-x64-portable.zip"
$names = @('CodexTrafficMonitor.exe', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt') +
    @(Get-ChildItem -LiteralPath $payload -Filter *.dll -File | ForEach-Object Name) +
    @(Get-ChildItem -LiteralPath (Join-Path $payload 'amd64') -Filter *.dll -File | ForEach-Object { 'amd64\' + $_.Name })
# Compress-Archive in Windows PowerShell 5.1 writes '\' separators; ZIP entries use '/'.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable -Force }
$archive = [IO.Compression.ZipFile]::Open($portable, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $names) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $payload $name), ($name -replace '\\', '/'), [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
$installer = Join-Path $destination "CodexTrafficMonitor-Setup-$version-win-x64.exe"
$checksums = @($installer, $portable) | ForEach-Object {
    $digest = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    $digest + '  ' + [IO.Path]::GetFileName($_)
}
$checksums | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding ascii
Write-Host "发布文件已生成：$destination"
