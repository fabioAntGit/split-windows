# Builds the whole solution (Release by default, like the installer build).
# Usage: ./build.ps1 [-Configuration Debug]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
# Shells started before a tool was installed miss it on PATH: read it from the registry.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
Set-Location $PSScriptRoot

# Restore needs the network, so it runs only when nothing was restored yet; builds stay offline.
if (-not (Test-Path tests/Split.Tests/obj/project.assets.json)) {
    dotnet restore Split.sln
    if ($LASTEXITCODE -ne 0) { Write-Error 'restore failed'; exit 1 }
}
dotnet build Split.sln --no-restore -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Error 'build failed'; exit 1 }