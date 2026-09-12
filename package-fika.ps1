param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath($PSScriptRoot)
[xml]$props = Get-Content -LiteralPath (Join-Path $workspace 'Directory.Build.props')
$version = $props.Project.PropertyGroup.ModVersion
if (!$SkipBuild) {
    dotnet build (Join-Path $workspace 'terminal-fika/terminal-fika.csproj') -c Release -p:SkipDeploy=true -p:SkipPackage=true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Terminal Fika build failed' }
}
$addon = Join-Path $workspace 'terminal-fika/bin/Release/netstandard2.1/ManimalTerminalFika.dll'
if (!(Test-Path -LiteralPath $addon)) { throw "Missing build: $addon" }
$output = Join-Path $workspace 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$archivePath = Join-Path $output "Manimal-Terminal-SPT-4.1-Fika-addon-$version.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Stream known files directly: no staging directory or recursive deletion needed.
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $entries = @{
        'BepInEx/plugins/ManimalTerminal/ManimalTerminalFika.dll' = $addon
    }
    foreach ($entry in $entries.GetEnumerator()) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value, $entry.Key, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose(); $stream.Dispose() }
Write-Host "Fika-only addon: $archivePath"
