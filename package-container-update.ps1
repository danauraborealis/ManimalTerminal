param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath($PSScriptRoot)
[xml]$props = Get-Content -LiteralPath (Join-Path $workspace 'Directory.Build.props')
$version = $props.Project.PropertyGroup.ModVersion
if (!$SkipBuild) {
    foreach ($project in @('terminal-server/terminal-server.csproj', 'terminal-client/terminal-client.csproj')) {
        dotnet build (Join-Path $workspace $project) -c Release -p:SkipDeploy=true -p:SkipPackage=true -p:UseSharedCompilation=false --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}
# A coordinated update for an existing Terminal installation. Explicit runtime
# files only; loose loot, configuration, documents and development files stay out.
$files = [ordered]@{
    'BepInEx/plugins/ManimalTerminal/ManimalTerminalClient.dll' = 'terminal-client/bin/Release/netstandard2.1/ManimalTerminalClient.dll'
    'BepInEx/plugins/ManimalTerminal/plugin-data/terminal_container_templates.json' = 'terminal-client/plugin-data/terminal_container_templates.json'
    'SPT_Runtime/user/mods/ManimalTerminal/terminal-server.dll' = 'terminal-server/bin/Release/terminal-server.dll'
}
foreach ($name in @('containerTemplates.json', 'containerLocales.json', 'staticContainers.json', 'staticLoot.json', 'staticAmmo.json')) {
    $files["SPT_Runtime/user/mods/ManimalTerminal/db/$name"] = "terminal-server/db/$name"
}
foreach ($source in $files.Values) {
    if (!(Test-Path -LiteralPath (Join-Path $workspace $source))) { throw "Missing runtime file: $source" }
}
$output = Join-Path $workspace 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$archivePath = Join-Path $output "Manimal-Terminal-SPT-4.1-container-update-$version.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $files.GetEnumerator()) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
            (Join-Path $workspace $entry.Value), $entry.Key, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose(); $stream.Dispose() }
[pscustomobject]@{Archive=$archivePath;Files=$files.Count;SHA256=(Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash} | ConvertTo-Json -Compress
