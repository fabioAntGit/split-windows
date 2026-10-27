# Downloads the fonts and icons once, from pinned releases, and checks their SHA-256.
# The results live in assets/fonts and are committed: nothing else ever needs the network.
# Usage: ./tools/get-assets.ps1 [-Force]   (-Force downloads again even when the files exist)
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
Add-Type -AssemblyName System.IO.Compression.FileSystem

$inter = 'https://github.com/rsms/inter/releases/download/v4.0/Inter-4.0.zip'
$interHash = 'FF970A5D4561A04F102A7CB781ADBD6AC4E9B6C460914C7A101F15ACB7F7D1A4'
# Static files, because WPF does not handle variable font axes well.
$interFiles = 'Inter-Regular.ttf', 'Inter-Medium.ttf', 'Inter-SemiBold.ttf', 'Inter-Bold.ttf'

$fluent = 'https://raw.githubusercontent.com/microsoft/fluentui-system-icons/1.1.344'
$fluentFiles = [ordered]@{
    'fonts/FluentSystemIcons-Regular.ttf'  = @('FluentSystemIcons-Regular.ttf',  'F977AE5BFB46661E7BA94B098E0D7720E27AF888E57B9B14E239933AC4C0E6F4')
    'fonts/FluentSystemIcons-Regular.json' = @('FluentSystemIcons-Regular.json', 'C6E91D8B156B6D29AFF3AECCD8D6B9752C2365C9F9AD02B194D5D791DA7ABEE8')
    'fonts/FluentSystemIcons-Filled.ttf'   = @('FluentSystemIcons-Filled.ttf',   '0A5ADCF88AB5B243096DA72D965BD22CD303200B36BA739A5A2A81086EF847AB')
    'fonts/FluentSystemIcons-Filled.json'  = @('FluentSystemIcons-Filled.json',  '0DB027F0C130330A2660034B0036D37FA68A206BEEA38FF59595502F75897546')
    'LICENSE'                              = @('LICENSE-FluentSystemIcons.txt',  '69BC45DC42B9ACB96A69823ADBC6AE538374E3C0BDE169B855B32C48EAAEF52F')
}

# .NET resolves relative paths against the process folder, not the PowerShell one.
$target = Join-Path $root 'assets/fonts'
$expected = @($interFiles) + 'OFL.txt' + @($fluentFiles.Values | ForEach-Object { $_[0] })
if (-not $Force -and -not ($expected | Where-Object { -not (Test-Path "$target/$_") })) {
    Write-Host 'assets/fonts is complete, nothing to download.'
    return
}
New-Item -ItemType Directory -Force $target | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) "split-assets-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $work | Out-Null

function Get-Verified($url, $hash, $path) {
    Invoke-WebRequest $url -OutFile $path -UseBasicParsing
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($actual -ne $hash) { throw "SHA-256 mismatch for $url`n  expected $hash`n  got      $actual" }
}

try {
    $zip = Join-Path $work 'inter.zip'
    Get-Verified $inter $interHash $zip
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            $name = if ($entry.FullName -eq 'LICENSE.txt') { 'OFL.txt' } elseif ($entry.FullName -like 'extras/ttf/*' -and $interFiles -contains $entry.Name) { $entry.Name }
            if ($name) { [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "$target/$name", $true) }
        }
    } finally { $archive.Dispose() }

    foreach ($remote in $fluentFiles.Keys) {
        $name, $hash = $fluentFiles[$remote]
        Get-Verified "$fluent/$remote" $hash "$target/$name"
    }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
$missing = $expected | Where-Object { -not (Test-Path "$target/$_") }
if ($missing) { throw "missing after download: $($missing -join ', ')" }
Write-Host "assets/fonts ready ($($expected.Count) files)."