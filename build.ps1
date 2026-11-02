# 构建并发布单文件 exe
# 用法: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\Cs2VoiceMusic\Cs2VoiceMusic.csproj'
$dist = Join-Path $root 'dist'

$dotnet = 'dotnet'
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) { $dotnet = $local }
    else { throw 'dotnet not found. Install .NET 8 SDK first.' }
}

Get-Process -Name Cs2VoiceMusic -ErrorAction SilentlyContinue | Stop-Process -Force

& $dotnet publish $proj -c Release -r win-x64 --self-contained true -o $dist
Write-Host "Output: $dist\Cs2VoiceMusic.exe"
