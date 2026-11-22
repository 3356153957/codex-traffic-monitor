param([switch]$SkipTests, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$taskRoot = $PSScriptRoot
$portableDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $portableDotnet) { $portableDotnet } else { 'dotnet' }
$project = Join-Path $taskRoot 'src\CodexTrafficMonitor.csproj'
$destination = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot 'dist' }
$testOutput = Join-Path $taskRoot 'output\self-test.json'
New-Item -ItemType Directory -Path (Join-Path $taskRoot 'output') -Force | Out-Null
# The installer/ZIP already compress downloads. Compressing assemblies again in
# the single-file host retains decompression buffers in the running process.
# Native DLLs (WPF, TraceEvent amd64\*) stay beside the EXE instead of being
# extracted to %TEMP%\.net, where any process of the user could replace them
# before the elevated monitor loads them.
& $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
# The compiler and MSBuild servers otherwise stay resident (~400 MB) after a one-off publish.
& $dotnet build-server shutdown | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.txt') -Destination $destination
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination $destination
if (-not $SkipTests) {
    $test = Start-Process -FilePath (Join-Path $destination 'CodexTrafficMonitor.exe') -ArgumentList @('--self-test', ('"' + $testOutput + '"')) -PassThru -Wait -WindowStyle Hidden
    if ($test.ExitCode -ne 0) { throw "自测失败，请查看 $testOutput" }
    Get-Content -Encoding UTF8 -LiteralPath $testOutput
}
Write-Host "已生成：$destination\CodexTrafficMonitor.exe"
