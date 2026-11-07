[CmdletBinding()]
param([string]$ToolsDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = if ($ToolsDirectory) { [IO.Path]::GetFullPath($ToolsDirectory) } else { Join-Path $projectRoot '.tools' }
$version = '7.1.1'
$directory = Join-Path $toolRoot "inno-setup-$version"
$compiler = Join-Path $directory 'compiler\ISCC.exe'
$source = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe'
$expectedHash = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'

function Assert-OfficialSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Pyrsys B\.V\.(,|$)') {
        throw "Inno Setup 官方签名验证失败：$Path（$($signature.Status)）。"
    }
}

function Assert-CompilerVersion([string]$Path) {
    $actual = (& $Path --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $version) { throw "Inno Setup 编译器版本不符合锁定版本：$actual" }
}

if (Test-Path -LiteralPath $compiler -PathType Leaf) {
    Assert-OfficialSignature $compiler
    Assert-CompilerVersion $compiler
    Write-Output $compiler
    exit 0
}
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$archive = Join-Path $directory "innosetup-$version-x64.exe"
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    Invoke-WebRequest -Uri $source -OutFile $archive
}
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw 'Inno Setup 下载 SHA-256 校验失败，未运行下载的文件。' }
Assert-OfficialSignature $archive
# /PORTABLE=1 installs only to this directory, without system registration,
# uninstall entries, file associations, or global compiler configuration.
$process = Start-Process -FilePath $archive -ArgumentList @(
    '/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES',
    '/NORESTART', '/SP-', ('/DIR="' + (Join-Path $directory 'compiler') + '"')
) -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Inno Setup 便携解包失败：$($process.ExitCode)" }
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '未找到解包后的 ISCC.exe。' }
Assert-OfficialSignature $compiler
Assert-CompilerVersion $compiler
Write-Output $compiler
