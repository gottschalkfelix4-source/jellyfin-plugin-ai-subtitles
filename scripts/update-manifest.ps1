# Trägt eine Plugin-Version in manifest.json (Jellyfin-Plugin-Repository) ein.
# Die Version kommt oben in die Liste; ein vorhandener Eintrag gleicher Version wird ersetzt.
# Aufruf:  ./scripts/update-manifest.ps1 -Version 1.0.0.0 -ZipPath artifacts/ai-subtitle-translator_1.0.0.0.zip
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$Repository = "gottschalkfelix4-source/jellyfin-plugin-ai-subtitles",
    [string]$TargetAbi = "12.4.0.0",
    [string]$Changelog = ""
)

$ErrorActionPreference = "Stop"
$guid = "7c3e9a51-2f4b-4d8e-9b61-5a0d2c8e4f17"
$manifestPath = Join-Path (Split-Path $PSScriptRoot -Parent) "manifest.json"
$zipName = Split-Path $ZipPath -Leaf

$entry = [ordered]@{
    version   = $Version
    changelog = $Changelog
    targetAbi = $TargetAbi
    sourceUrl = "https://github.com/$Repository/releases/download/v$Version/$zipName"
    checksum  = (Get-FileHash $ZipPath -Algorithm MD5).Hash.ToLowerInvariant()
    timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}

$older = @()
if (Test-Path $manifestPath) {
    $current = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    foreach ($plugin in @($current)) {
        if ($plugin.guid -eq $guid) {
            $older = @(@($plugin.versions) | Where-Object { $_.version -ne $Version })
        }
    }
}

$plugin = [ordered]@{
    guid        = $guid
    name        = "AI Subtitle Translator"
    description = "Übersetzt die vorhandenen Untertitel eines Films oder einer Folge live im Web-Player in eine beliebige Sprache. KI-Anbindung über OpenAI-kompatible Endpunkte (OpenAI, Ollama, LM Studio, OpenRouter …) oder die Anthropic-API (Claude). Übersetzungen lassen sich als normale Untertitelspur speichern."
    overview    = "KI-Übersetzung vorhandener Untertitel in jede Sprache"
    owner       = "gottschalkfelix4-source"
    category    = "Subtitles"
    imageUrl    = ""
    versions    = @(@($entry) + $older | Sort-Object -Property { [version]$_.version } -Descending)
}

$json = ConvertTo-Json -InputObject @($plugin) -Depth 10
[System.IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host "manifest.json aktualisiert: $Version ($($entry.checksum))"
