# Downloads the background-removal model once, verifies its SHA-256 and stores it in
# assets/models/ (ignored by git). The only network use besides `dotnet restore`.
$ErrorActionPreference = 'Stop'
$Url    = 'https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx'
$Sha256 = '60920E99C45464F2BA57BEE2AD08C919A52BBF852739E96947FBB4358C0D964A'
$Dest   = Join-Path $PSScriptRoot '..\assets\models\isnet-general-use.onnx'

function Test-Hash($path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -eq $Sha256 }

if ((Test-Path $Dest) -and (Test-Hash $Dest)) { Write-Host "Model already present: $Dest"; exit 0 }

New-Item -ItemType Directory -Force -Path (Split-Path $Dest) | Out-Null
$tmp = "$Dest.download"
try {
    Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing
    if (-not (Test-Hash $tmp)) { throw "SHA-256 mismatch for $Url (got $((Get-FileHash -Algorithm SHA256 $tmp).Hash))" }
    Move-Item -Force $tmp $Dest
} finally { if (Test-Path $tmp) { Remove-Item -Force $tmp } }
Write-Host "Model saved: $Dest"
