<#
.SYNOPSIS
  Builds, tests, publishes and zips Magpienest (portable, self-contained, win-x64).

.PARAMETER Version
  Optional version override (for example 0.1.0). When omitted, <Version> in
  Directory.Build.props is used.

.NOTES
  Output: artifacts\Magpienest-<version>-win-x64.zip
  Never enable PublishTrimmed, PublishSingleFile or PublishAot: they break WPF,
  reflection-based plugin discovery and per-plugin loading.
#>
[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Assert-Success([string]$what) {
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed with exit code $LASTEXITCODE."
    }
}

# Version: explicit parameter, else <Version> from Directory.Build.props.
$versionArgs = @()
if ($Version) {
    $versionArgs = @("-p:Version=$Version")
}
else {
    $props = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $Version) {
        throw 'No <Version> found in Directory.Build.props.'
    }
}
Write-Host "Publishing Magpienest $Version"

$publishDir = Join-Path $root 'artifacts\publish\Magpienest'
$zipPath = Join-Path $root "artifacts\Magpienest-$Version-win-x64.zip"
$releasePlugins = Join-Path $root 'artifacts\bin\Release\plugins'

# 1. Build and test (Release).
Write-Host '== 1/6 Build and test (Release) =='
& dotnet build Magpienest.slnx -c Release @versionArgs
Assert-Success 'dotnet build'
& dotnet test Magpienest.slnx -c Release --no-build
Assert-Success 'dotnet test'

# 2. Publish the host, self-contained.
Write-Host '== 2/6 Publish the host =='
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
& dotnet publish src/Magpienest.App -c Release -r win-x64 --self-contained true -o $publishDir @versionArgs
Assert-Success 'dotnet publish'

# 3. Plugins (framework-dependent, built next to the host by the Release build).
Write-Host '== 3/6 Copy plugins =='
if (-not (Test-Path -LiteralPath $releasePlugins)) {
    throw "Plugin folder not found: $releasePlugins"
}
$pluginsTarget = Join-Path $publishDir 'plugins'
if (Test-Path -LiteralPath $pluginsTarget) {
    Remove-Item -LiteralPath $pluginsTarget -Recurse -Force
}
Copy-Item -LiteralPath $releasePlugins -Destination $pluginsTarget -Recurse

# 4. Clean up and add the portable marker and documents.
Write-Host '== 4/6 Finish the folder =='
Get-ChildItem -LiteralPath $publishDir -Recurse -Filter '*.pdb' -File | Remove-Item -Force
# The LibVLC package content flows from the plugin project into the host publish; the plugin loads its
# own copy from plugins\magpienest.mediaengine.libvlc\libvlc, so the host-level duplicate (~100 MB) is dropped.
$hostLibVlc = Join-Path $publishDir 'libvlc'
if (Test-Path -LiteralPath $hostLibVlc) {
    Remove-Item -LiteralPath $hostLibVlc -Recurse -Force
}
# Plugins carry native builds for every platform (runtimes\<rid>); keep only win and win-x64.
$foreignRuntimes = @(Get-ChildItem -LiteralPath $pluginsTarget -Recurse -Directory -Filter 'runtimes' |
    ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory } |
    Where-Object { $_.Name -ne 'win' -and $_.Name -ne 'win-x64' })
foreach ($dir in $foreignRuntimes) {
    Remove-Item -LiteralPath $dir.FullName -Recurse -Force
}
# Empty marker file: portable mode keeps the data in data\ next to the exe.
[System.IO.File]::WriteAllBytes((Join-Path $publishDir 'portable.txt'), [byte[]]@())
foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.zh-TW.md') {
    $source = Join-Path $root $doc
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing required file: $doc"
    }
    Copy-Item -LiteralPath $source -Destination $publishDir -Force
}

# 5. Smoke test the published build with a throw-away data folder.
Write-Host '== 5/6 Smoke test =='
$smokeData = Join-Path ([System.IO.Path]::GetTempPath()) ('magpienest-smoke-' + [guid]::NewGuid().ToString('N'))
try {
    $exe = Join-Path $publishDir 'Magpienest.exe'
    $process = Start-Process -FilePath $exe -ArgumentList @('--smoke-test', '--data-dir', ('"{0}"' -f $smokeData)) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Smoke test failed with exit code $($process.ExitCode). Report: $smokeData\smoke.json"
    }
    Write-Host 'Smoke test passed.'
}
finally {
    if ((Test-Path -LiteralPath $smokeData) -and ($process -and $process.ExitCode -eq 0)) {
        Remove-Item -LiteralPath $smokeData -Recurse -Force
    }
}

# 6. Zip (ZipFile writes forward slashes; Compress-Archive in Windows PowerShell 5.1 does not).
Write-Host '== 6/6 Zip =='
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$sizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
Write-Host "Done: $zipPath ($sizeMb MB)"
