# Publishes Split and splitapp (self-contained) and builds one installer per architecture.
# Usage: ./make-installer.ps1 [-Arch x64,arm64]
#   -> build/installer/Split-<version>-<arch>-Setup.exe, and a .sha256 next to each.
# Needs the .NET SDK and Inno Setup 6 (ISCC.exe); nothing is downloaded. The restore that publishing needs
# for the win-x64 and win-arm64 runtimes uses the network once, when it has not been done before.
param(
    [ValidateSet('x64', 'arm64')][string[]]$Arch = @('x64', 'arm64'),
    # Only for the installer's own tests: a mutex name that a running Split does not hold.
    [string]$MutexName
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
Set-Location $PSScriptRoot

# The version lives in Directory.Build.props, and the program's About window reads it from there.
$version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Write-Error 'No <Version> in Directory.Build.props'; exit 1 }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { Write-Error 'Inno Setup 6 was not found (ISCC.exe). Install it (winget install -e --id JRSoftware.InnoSetup) and run this again.'; exit 1 }

if (-not (Test-Path assets/models/isnet-general-use.onnx)) {
    Write-Warning 'assets/models/isnet-general-use.onnx is missing (tools/get-model.ps1): this installer will not offer Remove background.'
}

$out = Join-Path $PSScriptRoot 'build/installer'
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($arch in $Arch) {
    $rid = "win-$arch"
    # Restore needs the network, so only when this runtime has not been restored yet.
    foreach ($project in 'src/Split.App', 'src/Split.Cli') {
        $assets = "$project/obj/project.assets.json"
        if (-not (Test-Path $assets) -or -not (Select-String -Path $assets -Pattern "/$rid" -Quiet)) {
            dotnet restore $project -r $rid
            if ($LASTEXITCODE -ne 0) { Write-Error "restore failed for $project ($rid)"; exit 1 }
        }
    }

    # One folder per architecture: Split.exe and splitapp.exe share the runtime and libraries in it.
    $stage = Join-Path $PSScriptRoot "build/publish/$arch"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    foreach ($project in 'src/Split.App', 'src/Split.Cli') {
        dotnet publish $project -c Release -r $rid --no-restore -o $stage
        if ($LASTEXITCODE -ne 0) { Write-Error "publish failed for $project ($rid)"; exit 1 }
    }
    # Debug symbols (the native ones weigh 110 MB) and import libraries are of no use to a user.
    Get-ChildItem $stage -Recurse -File -Include *.pdb, *.lib | Remove-Item

    $defines = @("/DAppVersion=$version", "/DArch=$arch", "/DSourceDir=$stage", "/DOutputDir=$out")
    if ($MutexName) { $defines += "/DMutexName=$MutexName" }
    & $iscc /Q @defines installer/Split.iss
    if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup failed for $arch"; exit 1 }

    $setup = Get-Item (Join-Path $out "Split-$version-$arch-Setup.exe")
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$($setup.FullName).sha256", "$hash  $($setup.Name)`n", (New-Object Text.UTF8Encoding $false))
    '{0}  {1:N0} MB' -f $setup.Name, ($setup.Length / 1MB)
}
