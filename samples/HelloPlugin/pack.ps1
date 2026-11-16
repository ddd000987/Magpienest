<#
.SYNOPSIS
  Builds the Hello plugin and packs it into HelloPlugin.zip beside this script.

.DESCRIPTION
  The zip contains plugin.json at its root, the plugin dll and the lang\ folder,
  and nothing from Magpienest itself (no Magpienest.Sdk.dll). Install it from
  Magpienest: Settings > Plugins > Install plugin...

  Works in Windows PowerShell 5.1 and PowerShell 7. The zip is written with
  System.IO.Compression directly because Compress-Archive in Windows PowerShell
  5.1 stores backslashes in entry names, which other tools reject.

.PARAMETER Configuration
  Debug or Release (default Release).

.PARAMETER MagpienestDir
  Folder that contains Magpienest.Sdk.dll (a built or installed Magpienest).
  Default: the repository's artifacts\bin\Debug.

.EXAMPLE
  .\pack.ps1
  .\pack.ps1 -Configuration Debug -MagpienestDir "C:\Program Files\Magpienest"
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$MagpienestDir
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$project = Join-Path $here 'HelloPlugin.csproj'
$zipPath = Join-Path $here 'HelloPlugin.zip'

$buildArgs = @('build', $project, '-c', $Configuration, '--nologo')
if ($MagpienestDir) {
    $buildArgs += "-p:MagpienestDir=$MagpienestDir"
}
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$out = Join-Path $here (Join-Path 'bin' $Configuration)
$files = New-Object System.Collections.Generic.List[object]
foreach ($name in @('plugin.json', 'HelloPlugin.dll', 'HelloPlugin.deps.json')) {
    $files.Add(@{ Source = (Join-Path $out $name); Entry = $name })
}
foreach ($lang in Get-ChildItem -LiteralPath (Join-Path $out 'lang') -Filter '*.json') {
    $files.Add(@{ Source = $lang.FullName; Entry = 'lang/' + $lang.Name })
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file.Source)) {
            throw "Missing build output: $($file.Source)"
        }
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $file.Source, $file.Entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $zip.Dispose()
}

Write-Host "Created $zipPath"
