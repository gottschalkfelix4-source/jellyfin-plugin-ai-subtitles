# Baut das Plugin und erzeugt ein installierbares ZIP unter artifacts\.
# Aufruf:  powershell -ExecutionPolicy Bypass -File .\package.ps1 [-Version 1.0.0.0]
param(
    [string]$Version = "1.0.0.0",
    [string]$TargetAbi = "12.4.0.0"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dotnet = if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { "$env:USERPROFILE\.dotnet\dotnet.exe" } else { "dotnet" }
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet" }

$publish = Join-Path $root "artifacts\publish"
$stage = Join-Path $root "artifacts\AI Subtitle Translator_$Version"
$zip = Join-Path $root "artifacts\ai-subtitle-translator_$Version.zip"

Remove-Item $publish, $stage, $zip -Recurse -Force -ErrorAction SilentlyContinue

& $dotnet publish (Join-Path $root "Jellyfin.Plugin.AiSubtitles\Jellyfin.Plugin.AiSubtitles.csproj") `
    -c Release -o $publish -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen" }

$assemblies = @("Jellyfin.Plugin.AiSubtitles.dll", "Anthropic.dll", "Microsoft.Extensions.AI.Abstractions.dll")
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($a in $assemblies) { Copy-Item (Join-Path $publish $a) $stage }

$meta = [ordered]@{
    category    = "Subtitles"
    changelog   = "Erste Version"
    description = "Übersetzt vorhandene Untertitel live im Player per KI (OpenAI-kompatibel oder Anthropic Claude)."
    guid        = "7c3e9a51-2f4b-4d8e-9b61-5a0d2c8e4f17"
    name        = "AI Subtitle Translator"
    overview    = "KI-Übersetzung vorhandener Untertitel in jede Sprache"
    owner       = "felix"
    targetAbi   = $TargetAbi
    timestamp   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    version     = $Version
    status      = "Active"
    autoUpdate  = $false
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
