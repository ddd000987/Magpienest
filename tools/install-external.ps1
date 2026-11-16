<#
.SYNOPSIS
  Copies the built external plugins (for example the web interface) into a Magpienest data folder, the same
  place Settings > Plugins > Install plugin... puts them. For smoke tests and screenshots.

.PARAMETER DataDir
  The data folder used with --data-dir (for example artifacts\smoke-data).

.PARAMETER Configuration
  Debug (default) or Release.

.PARAMETER Id
  Only this plugin id (default: every plugin under artifacts\bin\<Configuration>\external).

.EXAMPLE
  .\tools\install-external.ps1 -DataDir artifacts\smoke-data
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DataDir,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Id
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$external = Join-Path $root "artifacts\bin\$Configuration\external"
if (-not (Test-Path -LiteralPath $external)) {
    throw "No external plugins built: $external (run dotnet build first)."
}

$plugins = Join-Path $DataDir 'plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
foreach ($source in Get-ChildItem -LiteralPath $external -Directory) {
    if ($Id -and $source.Name -ne $Id) {
        continue
    }
    $target = Join-Path $plugins $source.Name
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    Copy-Item -LiteralPath $source.FullName -Destination $target -Recurse
    Write-Host "Installed $($source.Name) into $plugins"
}
