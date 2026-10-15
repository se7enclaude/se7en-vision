# Builds dist\se7en-Vision.exe: a single exe (cloudflared included) ready for a GitHub release / Seventh Hub.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'dist\publish'
$asset = Join-Path $root 'dist\se7en-Vision.exe'
$cloudflared = Join-Path $root 'tools\cloudflared.exe'

if (-not (Test-Path $cloudflared)) {
    Write-Host 'Téléchargement de cloudflared (releases officielles Cloudflare)...'
    New-Item -ItemType Directory -Force (Split-Path $cloudflared) | Out-Null
    Invoke-WebRequest 'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe' -OutFile $cloudflared
}
$signer = (Get-AuthenticodeSignature $cloudflared)
if ($signer.Status -ne 'Valid' -or $signer.SignerCertificate.Subject -notmatch 'Cloudflare, Inc\.') {
    throw 'cloudflared.exe n''est pas signé par Cloudflare'
}

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

dotnet publish (Join-Path $root 'src\PhoneRemote\PhoneRemote.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a échoué' }

Copy-Item (Join-Path $publish 'se7en Vision.exe') $asset -Force
$hash = (Get-FileHash $asset -Algorithm SHA256).Hash
$version = (Get-Item $asset).VersionInfo.ProductVersion
Write-Host ''
Write-Host "Prêt : $asset (version $version)"
Write-Host "SHA-256 : $hash"
