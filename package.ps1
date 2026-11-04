# Baut das Plugin und erzeugt das installierbare ZIP unter artifacts/.
# Läuft mit Windows PowerShell 5.1 und PowerShell 7 (auch unter Linux in GitHub Actions).
# Aufruf:  powershell -ExecutionPolicy Bypass -File ./package.ps1 -Version 1.0.0.0
param(
    [string]$Version = "1.0.0.0",
    [string]$TargetAbi = "12.4.0.0",
    [string]$Changelog = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# .NET 10 SDK: lokale Benutzerinstallation bevorzugen, sonst dotnet aus dem PATH.
$dotnet = "dotnet"
if ($env:USERPROFILE -and (Test-Path (Join-Path $env:USERPROFILE ".dotnet/dotnet.exe"))) {
    $env:DOTNET_ROOT = Join-Path $env:USERPROFILE ".dotnet"
    $dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe"
}

$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
$stage = Join-Path $artifacts "AI Subtitle Translator_$Version"
$zip = Join-Path $artifacts "ai-subtitle-translator_$Version.zip"

Remove-Item $publish, $stage, $zip -Recurse -Force -ErrorAction SilentlyContinue

$project = Join-Path (Join-Path $root "Jellyfin.Plugin.AiSubtitles") "Jellyfin.Plugin.AiSubtitles.csproj"
& $dotnet publish $project -c Release -o $publish "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen" }

$assemblies = @("Jellyfin.Plugin.AiSubtitles.dll", "Anthropic.dll", "Microsoft.Extensions.AI.Abstractions.dll")
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($a in $assemblies) { Copy-Item (Join-Path $publish $a) $stage }

$meta = [ordered]@{
    category    = "Subtitles"
    changelog   = $Changelog
    description = "Übersetzt vorhandene Untertitel live im Player per KI (OpenAI-kompatibel oder Anthropic Claude)."
    guid        = "7c3e9a51-2f4b-4d8e-9b61-5a0d2c8e4f17"
    name        = "AI Subtitle Translator"
    overview    = "KI-Übersetzung vorhandener Untertitel in jede Sprache"
    owner       = "gottschalkfelix4-source"
    targetAbi   = $TargetAbi
    timestamp   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    version     = $Version
    status      = "Active"
    autoUpdate  = $true
    imagePath   = ""
    assemblies  = $assemblies
}
[System.IO.File]::WriteAllText((Join-Path $stage "meta.json"), ($meta | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "Plugin-Ordner: $stage"
Write-Host "ZIP:           $zip"
Write-Host "MD5:           $md5"
