# Builds, then runs the tests (or those whose "suite name" contains the filter).
# Usage: ./test.ps1 [filter]   (e.g. ./test.ps1 Errors)
#   $env:SPLIT_OFFICE=1 also runs the suites that need Word, Excel and PowerPoint.
param([string]$Filter)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& ./build.ps1
if ($LASTEXITCODE -ne 0) { exit 1 }

# The messages the tests read are the English source texts.
$env:SPLIT_LANGUAGE = 'en'
$runner = 'tests/Split.Tests/bin/Release/net10.0-windows10.0.19041.0/Split.Tests.exe'
if ($Filter) { & $runner $Filter } else { & $runner }
exit $LASTEXITCODE