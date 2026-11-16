<#
.SYNOPSIS
  Builds, tests, publishes and zips Magpienest (portable, self-contained, win-x64), and builds the plugin store.

.PARAMETER Version
  Optional version override (for example 0.1.0). When omitted, <Version> in
  Directory.Build.props is used.

.PARAMETER StoreRepository
  The store repository as owner/name, used for the package addresses in catalog.json.
  When omitted, OWNER/Magpienest-Store is written and a warning is shown.

.PARAMETER PreviousCatalog
  Optional: the catalog.json currently published in the store repository. A bundle whose version is unchanged
  keeps its old entry (its zips are not uploaded again); the others are listed as needing a new release.
  Without it every store bundle is listed.

.NOTES
  Output: artifacts\Magpienest-<version>-win-x64.zip (without the store bundles), and artifacts\store\ with one
  <plugin id>-<version>.zip per store plugin, catalog.json and the store repository's READMEs.
  The script never contacts the network.
  Never enable PublishTrimmed, PublishSingleFile or PublishAot: they break WPF,
  reflection-based plugin discovery and per-plugin loading.
  Keep this file ASCII: Windows PowerShell 5.1 reads a script without a BOM as ANSI.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$StoreRepository,
    [string]$PreviousCatalog
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

if (-not $StoreRepository) {
    $StoreRepository = 'OWNER/Magpienest-Store'
    Write-Warning "No -StoreRepository given; catalog.json uses the placeholder $StoreRepository."
}
elseif ($StoreRepository -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') {
    throw '-StoreRepository must look like owner/name.'
}
# Read it before artifacts\store is cleaned (the previous catalogue may live there).
$previous = $null
if ($PreviousCatalog) {
    $previous = Get-Content -LiteralPath $PreviousCatalog -Raw -Encoding UTF8 | ConvertFrom-Json
}

# Store bundles and their plugins in install order: a dependency before the plugin that needs it.
# Every plugin.json "bundle" must agree with this list; the check is in step 3.
$storeBundles = [ordered]@{
    'magpienest.imagewindow' = @('magpienest.imagewindow')
    'magpienest.video'       = @('magpienest.mediaengine.libvlc', 'magpienest.video')
    'magpienest.game'        = @('magpienest.game')
    'magpienest.webui'       = @('magpienest.webui')
}

$publishDir = Join-Path $root 'artifacts\publish\Magpienest'
$zipPath = Join-Path $root "artifacts\Magpienest-$Version-win-x64.zip"
$releasePlugins = Join-Path $root 'artifacts\bin\Release\plugins'
$releaseExternal = Join-Path $root 'artifacts\bin\Release\external'
$utf8 = New-Object System.Text.UTF8Encoding $false

function Remove-ForeignRuntimes([string]$path) {
    # Plugins carry native builds for every platform (runtimes\<rid>); keep only win and win-x64.
    $foreign = @(Get-ChildItem -LiteralPath $path -Recurse -Directory -Filter 'runtimes' |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory } |
        Where-Object { $_.Name -ne 'win' -and $_.Name -ne 'win-x64' })
    foreach ($dir in $foreign) {
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force
    }
}

# 1. Build and test (Release).
Write-Host '== 1/7 Build and test (Release) =='
& dotnet build Magpienest.slnx -c Release @versionArgs
Assert-Success 'dotnet build'
& dotnet test Magpienest.slnx -c Release --no-build
Assert-Success 'dotnet test'

# 2. Publish the host, self-contained.
Write-Host '== 2/7 Publish the host =='
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
& dotnet publish src/Magpienest.App -c Release -r win-x64 --self-contained true -o $publishDir @versionArgs
Assert-Success 'dotnet publish'

# 3. Plugins (framework-dependent, built by the Release build). Store bundles stay out of the main zip.
Write-Host '== 3/7 Copy plugins =='
if (-not (Test-Path -LiteralPath $releasePlugins)) {
    throw "Plugin folder not found: $releasePlugins"
}
$pluginsTarget = Join-Path $publishDir 'plugins'
if (Test-Path -LiteralPath $pluginsTarget) {
    Remove-Item -LiteralPath $pluginsTarget -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pluginsTarget | Out-Null

# Every plugin of the Release build: id -> folder and manifest.
$allPlugins = [ordered]@{}
$folders = @(Get-ChildItem -LiteralPath $releasePlugins -Directory)
if (Test-Path -LiteralPath $releaseExternal) {
    $folders += @(Get-ChildItem -LiteralPath $releaseExternal -Directory)
}
foreach ($folder in $folders) {
    # -Encoding UTF8: Windows PowerShell 5.1 would read the BOM-less file as ANSI and break the Chinese texts.
    $manifest = Get-Content -LiteralPath (Join-Path $folder.FullName 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($allPlugins.Contains($manifest.id)) {
        throw "Plugin $($manifest.id) was built twice."
    }
    $allPlugins[$manifest.id] = [pscustomobject]@{ Folder = $folder.FullName; Manifest = $manifest }
}

foreach ($bundle in $storeBundles.Keys) {
    foreach ($id in $storeBundles[$bundle]) {
        if (-not $allPlugins.Contains($id)) {
            throw "Store bundle $bundle lists $id, which the Release build did not produce."
        }
        if ($allPlugins[$id].Manifest.bundle -ne $bundle) {
            throw "Store bundle $bundle lists $id, but its plugin.json says bundle '$($allPlugins[$id].Manifest.bundle)'."
        }
    }
}
foreach ($id in $allPlugins.Keys) {
    $plugin = $allPlugins[$id]
    $bundle = $plugin.Manifest.bundle
    if ($bundle -and $storeBundles.Contains($bundle)) {
        if ($storeBundles[$bundle] -notcontains $id) {
            throw "Plugin $id is in store bundle $bundle but missing from `$storeBundles."
        }
        continue
    }
    if ($plugin.Folder.StartsWith($releaseExternal, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "External plugin $id is not in a store bundle."
    }
    Copy-Item -LiteralPath $plugin.Folder -Destination (Join-Path $pluginsTarget $id) -Recurse
}

# 4. Clean up and add the portable marker and documents.
Write-Host '== 4/7 Finish the folder =='
Get-ChildItem -LiteralPath $publishDir -Recurse -Filter '*.pdb' -File | Remove-Item -Force
# The LibVLC package content flows from the plugin project into the host publish; the plugin (a store
# package) brings its own copy in libvlc\, so the host-level duplicate (~100 MB) is dropped.
$hostLibVlc = Join-Path $publishDir 'libvlc'
if (Test-Path -LiteralPath $hostLibVlc) {
    Remove-Item -LiteralPath $hostLibVlc -Recurse -Force
}
Remove-ForeignRuntimes $pluginsTarget
# Empty marker file: portable mode keeps the data in data\ next to the exe.
[System.IO.File]::WriteAllBytes((Join-Path $publishDir 'portable.txt'), [byte[]]@())
foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.zh-TW.md') {
    $source = Join-Path $root $doc
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing required file: $doc"
    }
    Copy-Item -LiteralPath $source -Destination $publishDir -Force
}

# 5. Store: one package per store plugin (plugin.json at the root), catalog.json and the
#    store repository's READMEs, all in artifacts\store.
Write-Host '== 5/7 Store packages and catalogue =='
Add-Type -AssemblyName System.IO.Compression.FileSystem
$storeDir = Join-Path $root 'artifacts\store'
$storeStage = Join-Path $root 'artifacts\publish\store'
foreach ($dir in $storeDir, $storeStage) {
    if (Test-Path -LiteralPath $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

# Bundle names and descriptions come from the Settings plugin, so nothing is written twice.
$settingsLang = Join-Path $root 'src\plugins\Magpienest.Plugins.Settings\lang'
$texts = [ordered]@{}
foreach ($language in 'en', 'zh-TW') {
    $texts[$language] = Get-Content -LiteralPath (Join-Path $settingsLang "$language.json") -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Get-BundleText([string]$bundle, [string]$part) {
    $result = [ordered]@{}
    foreach ($language in $texts.Keys) {
        $property = $texts[$language].PSObject.Properties["Settings.Bundle.$bundle.$part"]
        if (-not $property -or -not $property.Value) {
            throw "Settings lang\$language.json has no Settings.Bundle.$bundle.$part."
        }
        $result[$language] = $property.Value
    }
    return $result
}

$catalogueBundles = @()
$storePackages = @()    # every store zip in install order, for the smoke test
$needsRelease = @()
foreach ($bundle in $storeBundles.Keys) {
    $members = @($storeBundles[$bundle] | ForEach-Object { $allPlugins[$_] })
    $bundleVersion = $members[0].Manifest.version
    $apiVersion = $members[0].Manifest.apiVersion
    foreach ($member in $members) {
        if ($member.Manifest.version -ne $bundleVersion -or $member.Manifest.apiVersion -ne $apiVersion) {
            throw "Store bundle $bundle mixes versions: every member needs the same version and apiVersion."
        }
    }

    $tag = "$bundle-$bundleVersion"
    $packages = @()
    foreach ($member in $members) {
        $id = $member.Manifest.id
        $stage = Join-Path $storeStage $id
        Copy-Item -LiteralPath $member.Folder -Destination $stage -Recurse
        Get-ChildItem -LiteralPath $stage -Recurse -Filter '*.pdb' -File | Remove-Item -Force
        Remove-ForeignRuntimes $stage
        foreach ($forbidden in 'Magpienest.Sdk.dll', 'Magpienest.Core.dll', 'Magpienest.exe', 'Magpienest.dll') {
            if (Test-Path -LiteralPath (Join-Path $stage $forbidden)) {
                throw "$id must not contain $forbidden."
            }
        }
        # Licences travel with the package (LibVLC is LGPL and is now distributed only through the store).
        foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
            Copy-Item -LiteralPath (Join-Path $root $doc) -Destination $stage -Force
        }
        $packagePath = Join-Path $storeDir "$id-$bundleVersion.zip"
        [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $packagePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        $storePackages += $packagePath
        $item = Get-Item -LiteralPath $packagePath
        $packages += [ordered]@{
            id     = $id
            size   = $item.Length
            url    = "https://github.com/$StoreRepository/releases/download/$tag/$($item.Name)"
            sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        Write-Host ('Packed {0} ({1} MB)' -f $item.Name, [math]::Round($item.Length / 1MB, 1))
    }

    $entry = [ordered]@{
        id          = $bundle
        name        = Get-BundleText $bundle 'Name'
        description = Get-BundleText $bundle 'Description'
        author      = $members[0].Manifest.author
        version     = $bundleVersion
        apiVersion  = $apiVersion
        plugins     = $packages
    }
    # An unchanged bundle keeps its previous entry, so its released zips stay valid and are not uploaded again.
    $old = $null
    if ($previous) {
        $old = @($previous.bundles | Where-Object { $_.id -eq $bundle }) | Select-Object -First 1
    }
    if ($old -and $old.version -eq $bundleVersion) {
        $entry = $old
    }
    else {
        $needsRelease += $tag
    }
    $catalogueBundles += $entry
}

$catalogue = [ordered]@{ schema = 1; bundles = $catalogueBundles }
$json = ConvertTo-Json -InputObject $catalogue -Depth 10
[System.IO.File]::WriteAllText((Join-Path $storeDir 'catalog.json'), $json.Replace("`r`n", "`n") + "`n", $utf8)

$mainUrl = 'https://github.com/{0}/Magpienest' -f $StoreRepository.Split('/')[0]
foreach ($readme in 'README.md', 'README.zh-TW.md') {
    $template = [System.IO.File]::ReadAllText((Join-Path $root "build\store\$readme"), $utf8)
    [System.IO.File]::WriteAllText((Join-Path $storeDir $readme), $template.Replace('{MAIN_URL}', $mainUrl), $utf8)
}

if ($needsRelease.Count -eq 0) {
    Write-Host 'Store: no bundle needs a new release.'
}
else {
    Write-Host 'Store: these bundles need a new release (tag = the name below; assets = its zips in artifacts\store):'
    $needsRelease | ForEach-Object { Write-Host "  $_" }
}

# 6. Smoke tests with throw-away data folders: the main zip alone, then with every store package installed.
Write-Host '== 6/7 Smoke test =='
$exe = Join-Path $publishDir 'Magpienest.exe'
function New-SmokeData {
    return Join-Path ([System.IO.Path]::GetTempPath()) ('magpienest-smoke-' + [guid]::NewGuid().ToString('N'))
}
function Get-FreePort {
    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    return $port
}

$smokeData = New-SmokeData
$process = Start-Process -FilePath $exe -ArgumentList @('--smoke-test', '--data-dir', ('"{0}"' -f $smokeData)) -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Smoke test failed with exit code $($process.ExitCode). Report: $smokeData\smoke.json"
}
Remove-Item -LiteralPath $smokeData -Recurse -Force
Write-Host 'Smoke test passed.'

# --install-plugin (DevTools) installs the packages the way the store does, without a dialog.
$smokeData = New-SmokeData
$installArgs = @('--data-dir', ('"{0}"' -f $smokeData), '--webui-port', (Get-FreePort))
foreach ($package in $storePackages) {
    $installArgs += @('--install-plugin', ('"{0}"' -f $package))
}
$process = Start-Process -FilePath $exe -ArgumentList $installArgs -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Installing the store packages failed with exit code $($process.ExitCode). Log: $smokeData\logs"
}
$process = Start-Process -FilePath $exe -ArgumentList @('--smoke-test', '--data-dir', ('"{0}"' -f $smokeData), '--webui-port', (Get-FreePort)) -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Smoke test with the store packages failed with exit code $($process.ExitCode). Report: $smokeData\smoke.json"
}
Remove-Item -LiteralPath $smokeData -Recurse -Force
Write-Host 'Smoke test with the store packages passed.'

# 7. Zip (ZipFile writes forward slashes; Compress-Archive in Windows PowerShell 5.1 does not).
Write-Host '== 7/7 Zip =='
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$sizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
Write-Host "Done: $zipPath ($sizeMb MB)"
Write-Host "Store: $storeDir (catalog.json, README.md, README.zh-TW.md and $($storePackages.Count) packages)"
