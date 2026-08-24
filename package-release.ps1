# builds all three halves in Release and produces THE release zip in dist\ -- single
# archive, same format as the other manimal mods: BepInEx\ + SPT\ trees at the root,
# extracted over the SPT install root. NOTHING under EscapeFromTarkov_Data (forge
# rule) -- the map bundles ride the plugin folder's streamingassets/ payload and the
# client materializes them at startup, exactly like icebreaker.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# version + spt path from the single source of truth
[xml]$props = Get-Content "$root\Directory.Build.props"
$ver = $props.Project.PropertyGroup.ModVersion
$spt = $props.Project.PropertyGroup.SPTPath

Write-Host "=== building client + prepatch + server (Release) ===" -ForegroundColor Cyan
dotnet build "$root\terminal-client\terminal-client.csproj" -c Release -v m
if ($LASTEXITCODE -ne 0) { throw "client build failed" }
dotnet build "$root\terminal-prepatch\terminal-prepatch.csproj" -c Release -v m
if ($LASTEXITCODE -ne 0) { throw "prepatch build failed" }
dotnet build "$root\terminal-server\terminal-server.csproj" -c Release -v m
if ($LASTEXITCODE -ne 0) { throw "server build failed" }

# NOTE: unlike icebreaker, terminal's REPO plugin-data is the source of truth —
# the client build above just deployed it repo -> live, so the live dir staged
# below is already fresh. no mirror-back step (it could clobber the repo).

Write-Host "=== staging ===" -ForegroundColor Cyan
$stage = "$root\obj-release-stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

# BepInEx\plugins payload: the LIVE dev deploy is the canonical copy of the authored
# data (plugin-data sidecars, terminal_fx.bundle, the in-plugin streamingassets map
# bundles, PerfectCullingRuntime). dev debris stays out.
$pluginDst = "$stage\BepInEx\plugins\ManimalTerminal"
New-Item -ItemType Directory -Force $pluginDst | Out-Null
Copy-Item "$spt\BepInEx\plugins\ManimalTerminal\*" $pluginDst -Recurse -Force
Remove-Item "$pluginDst\dumps" -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $pluginDst -Recurse -File -Filter '*.bak*' | Remove-Item -Force
Get-ChildItem $pluginDst -Recurse -File -Filter '*.broken.bak' | Remove-Item -Force -ErrorAction SilentlyContinue
# fresh DLL from this build, not whatever the deploy dir held
Copy-Item "$root\terminal-client\bin\Release\netstandard2.1\ManimalTerminalClient.dll" $pluginDst -Force

# BepInEx\patchers payload: the civilian WildSpawnType prepatcher
$patchDst = "$stage\BepInEx\patchers\ManimalTerminalPrepatch"
New-Item -ItemType Directory -Force $patchDst | Out-Null
Copy-Item "$root\terminal-prepatch\bin\Release\net471\ManimalTerminalPrepatch.dll" $patchDst -Force

# SPT\user\mods payload: db + bundles.json from the repo (their source of truth)
$serverDst = "$stage\SPT\user\mods\ManimalTerminal"
New-Item -ItemType Directory -Force $serverDst | Out-Null
Copy-Item "$root\terminal-server\db" "$serverDst\db" -Recurse -Force
Copy-Item "$root\terminal-server\bundles.json" $serverDst -Force
Copy-Item "$root\terminal-server\bin\Release\terminal-server.dll" $serverDst -Force

Copy-Item "$root\docs\RELEASE-README.txt" "$stage\README.txt" -Force
# forge requires the license file INSIDE the archive, not merely in the repo
Copy-Item "$root\LICENSE" "$stage\LICENSE" -Force

Write-Host "=== zipping (the scene bundle makes this take a few minutes) ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$zip = "$root\dist\Manimal-Terminal-$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# entry-by-entry, NOT CreateFromDirectory: powershell 5.1's framework build writes
# backslash separators into entry names, which is off-spec and trips some extractors
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1) -replace '\\', '/'
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }

# UPDATE ZIP: same tree minus the giant scene bundle under streamingassets/.
# for players who already have the map bundle and just want the latest DLLs /
# plugin-data / db updates. small (~100 MB range) vs the full ~4 GB archive.
Write-Host "=== zipping update archive (no scene bundle) ===" -ForegroundColor Cyan
$updateZip = "$root\dist\Manimal-Terminal-update-$ver.zip"
if (Test-Path $updateZip) { Remove-Item $updateZip -Force }
$updateArchive = [System.IO.Compression.ZipFile]::Open($updateZip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1) -replace '\\', '/'
        # skip the streamingassets tree wholesale — that's where the multi-GB
        # scene bundle lives, and it's what returning players already have
        if ($rel -like 'BepInEx/plugins/ManimalTerminal/streamingassets/*') { return }
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($updateArchive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $updateArchive.Dispose() }

Remove-Item -Recurse -Force $stage

# THE VIRUSTOTAL ARCHIVE. forge wants a scan link per version; the release zip is
# gigabytes of unity asset bundles, so scan the binaries alone and link the results.
# PerfectCullingRuntime.dll rides along because we REDISTRIBUTE it (stock, unmodified,
# asset store) -- the rule is about what ships, not what we wrote.
Write-Host "=== packaging binaries for virustotal ===" -ForegroundColor Cyan
$vtStage = "$root\obj-vt-stage"
if (Test-Path $vtStage) { Remove-Item -Recurse -Force $vtStage }
New-Item -ItemType Directory -Force $vtStage | Out-Null
@(
    "$root\terminal-client\bin\Release\netstandard2.1\ManimalTerminalClient.dll",
    "$root\terminal-prepatch\bin\Release\net471\ManimalTerminalPrepatch.dll",
    "$root\terminal-server\bin\Release\terminal-server.dll",
    "$spt\BepInEx\plugins\ManimalTerminal\PerfectCullingRuntime.dll"
) | ForEach-Object {
    if (-not (Test-Path $_)) { throw "virustotal archive: missing $_ -- build all three projects first" }
    Copy-Item $_ $vtStage -Force
}

# BACKSTOP: if a new binary ever lands in the shipped dirs, catch it here rather than
# letting it go out unscanned.
$shipped = @(
    Get-ChildItem "$spt\BepInEx\plugins\ManimalTerminal" -Recurse -Include *.dll, *.exe -File
    Get-ChildItem "$spt\BepInEx\patchers\ManimalTerminalPrepatch" -Recurse -Include *.dll, *.exe -File -ErrorAction SilentlyContinue
    Get-ChildItem "$spt\SPT\user\mods\ManimalTerminal" -Recurse -Include *.dll, *.exe -File -ErrorAction SilentlyContinue
) | Select-Object -ExpandProperty Name -Unique
$staged = Get-ChildItem $vtStage -File | Select-Object -ExpandProperty Name
$unscanned = $shipped | Where-Object { $_ -notin $staged }
if ($unscanned) { throw "binaries shipped but NOT in the virustotal archive: $($unscanned -join ', ')" }
$vtZip = "$root\dist\Manimal-Terminal-binaries-$ver.zip"
if (Test-Path $vtZip) { Remove-Item $vtZip -Force }
$va = [System.IO.Compression.ZipFile]::Open($vtZip, 'Create')
try {
    Get-ChildItem $vtStage -File | ForEach-Object {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($va, $_.FullName, $_.Name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $va.Dispose() }
Remove-Item -Recurse -Force $vtStage

# sha256 of everything shipped: paste alongside the scan link so anyone can confirm
# the dll they downloaded is the dll that was scanned
Write-Host "=== sha256 (for the release notes) ===" -ForegroundColor Cyan
Get-ChildItem "$root\dist" -Filter *.zip | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Tee-Object -FilePath "$root\dist\SHA256SUMS.txt"

Write-Host "=== dist ===" -ForegroundColor Green
Get-ChildItem "$root\dist" | Format-Table Name, @{n='Size';e={'{0:N1} MB' -f ($_.Length/1MB)}}
