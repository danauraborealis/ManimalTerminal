param(
    [string]$AssetSourcePath,
    [switch]$SkipBuild,
    [switch]$UpdateOnly
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath($PSScriptRoot)
[xml]$props = Get-Content -LiteralPath (Join-Path $workspace 'Directory.Build.props')
$version = $props.Project.PropertyGroup.ModVersion
if (!$AssetSourcePath) {
    $AssetSourcePath = Join-Path $props.Project.PropertyGroup.SPTPath.InnerText 'BepInEx/plugins/ManimalTerminal'
}
$AssetSourcePath = [IO.Path]::GetFullPath($AssetSourcePath)
if (!$SkipBuild) {
    foreach ($project in @('terminal-client', 'terminal-prepatch', 'terminal-server')) {
        dotnet build (Join-Path $workspace "$project/$project.csproj") -c Release -p:SkipDeploy=true -p:SkipPackage=true --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw "$project build failed" }
    }
}

# Enumerate runtime files explicitly. Authored repo data overrides the asset
# source; no live DLLs, README files, disabled bakes or development dumps ship.
$plugin = 'BepInEx/plugins/ManimalTerminal'
$server = 'SPT_Runtime/user/mods/ManimalTerminal'
$files = [ordered]@{
    "$plugin/ManimalTerminalClient.dll" = Join-Path $workspace 'terminal-client/bin/Release/netstandard2.1/ManimalTerminalClient.dll'
    "$plugin/PerfectCullingRuntime.dll" = Join-Path $workspace 'terminal-client/lib/PerfectCullingRuntime.dll'
    'BepInEx/patchers/ManimalTerminalPrepatch/ManimalTerminalPrepatch.dll' = Join-Path $workspace 'terminal-prepatch/bin/Release/net471/ManimalTerminalPrepatch.dll'
    "$server/terminal-server.dll" = Join-Path $workspace 'terminal-server/bin/Release/terminal-server.dll'
    "$server/bundles.json" = Join-Path $workspace 'terminal-server/bundles.json'
}
function Add-RuntimeTree([string]$source, [string]$destination, [string[]]$extensions) {
    if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "Missing runtime assets: $source" }
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File | Sort-Object FullName) {
        if ($file.Extension -notin $extensions) { continue }
        $relative = $file.FullName.Substring($source.Length + 1).Replace('\', '/')
        $files["$destination/$relative"] = $file.FullName
    }
}
Add-RuntimeTree (Join-Path $workspace 'terminal-client/plugin-data') "$plugin/plugin-data" @('.json','.png','.wav','.ogg','.mp4','.audiobakedata')
Add-RuntimeTree (Join-Path $workspace 'terminal-server/db') "$server/db" @('.json','.jsonc','.png','.jpg')
foreach ($bundle in @('terminal_fx.bundle','terminal_grass.bundle')) {
    $files["$plugin/$bundle"] = Join-Path $AssetSourcePath $bundle
}
Add-RuntimeTree (Join-Path $AssetSourcePath 'culling') "$plugin/culling" @('.pcbake')
if (!$UpdateOnly) {
    Add-RuntimeTree (Join-Path $AssetSourcePath 'streamingassets') "$plugin/streamingassets" @('.bundle')
    foreach ($required in @('terminal_scenes.bundle','terminal_preset.bundle')) {
        $found = $false
        foreach ($source in $files.Values) { if ([IO.Path]::GetFileName($source) -eq $required) { $found=$true; break } }
        if (!$found) { throw "Missing map bundle $required under $AssetSourcePath/streamingassets" }
    }
}
foreach ($source in $files.Values) {
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing runtime file: $source" }
}
$output = Join-Path $workspace 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-RuntimeArchive([string]$name, [bool]$omitMap, [bool]$binariesOnly = $false) {
    $path = Join-Path $output $name
    $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    $count = 0
    try {
        foreach ($entry in $files.GetEnumerator()) {
            if ($omitMap -and $entry.Key.StartsWith("$plugin/streamingassets/")) { continue }
            # This client repair needs its authored settings and clips even when
            # updating an existing install through the compact binaries archive.
            $vegetation = $entry.Key -eq "$plugin/plugin-data/terminal_vegetation.json" -or $entry.Key.StartsWith("$plugin/plugin-data/vegetation/")
            if ($binariesOnly -and !($entry.Key.EndsWith('.dll')) -and !$vegetation) { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value, $entry.Key, [IO.Compression.CompressionLevel]::Optimal)
            $count++
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
    Write-Host "$path ($count runtime files)"
    return $path
}
$archives = [Collections.Generic.List[string]]::new()
if (!$UpdateOnly) {
    $archives.Add((Write-RuntimeArchive "Manimal-Terminal-SPT-4.1-$version.zip" $false))
}
$archives.Add((Write-RuntimeArchive "Manimal-Terminal-SPT-4.1-update-$version.zip" $true))
$archives.Add((Write-RuntimeArchive "Manimal-Terminal-SPT-4.1-binaries-$version.zip" $true $true))
$hashes = foreach ($path in $archives) { "{0}  {1}" -f (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash, [IO.Path]::GetFileName($path) }
$hashes | Set-Content -LiteralPath (Join-Path $output "SHA256SUMS-SPT-4.1-$version.txt")
$hashes
