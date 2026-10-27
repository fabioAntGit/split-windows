# Builds the whole solution (Release by default, like the installer build).
# Usage: ./build.ps1 [-Configuration Debug]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
# Shells started before a tool was installed miss it on PATH: read it from the registry.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
Set-Location $PSScriptRoot

# Restore needs the network, so it runs only when nothing was restored yet or a project or
# package file changed since (a new package version); builds stay offline.
$assets = 'tests/Split.Tests/obj/project.assets.json'
$stale = -not (Test-Path $assets)
if (-not $stale) {
    $restored = (Get-Item $assets).LastWriteTime
    $files = @(Get-ChildItem src, tests -Recurse -File -Include *.csproj, *.props | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) + @(Get-ChildItem *.props)
    $stale = [bool]($files | Where-Object { $_.LastWriteTime -gt $restored } | Select-Object -First 1)
}
if ($stale) {
    dotnet restore Split.sln
    if ($LASTEXITCODE -ne 0) { Write-Error 'restore failed'; exit 1 }
}
dotnet build Split.sln --no-restore -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Error 'build failed'; exit 1 }