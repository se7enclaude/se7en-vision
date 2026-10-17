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

# FFmpeg (Gyan.dev « essentials », GPLv3) : encode l'écran en vidéo H.264. Version et empreinte fixées.
$ffmpeg = Join-Path $root 'tools\ffmpeg\ffmpeg.exe'
if (-not (Test-Path $ffmpeg)) {
    $ffVersion = '9.0.2'
    $ffSha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
    $zip = Join-Path $env:TEMP "ffmpeg-$ffVersion-essentials_build.zip"
    Write-Host "Téléchargement de FFmpeg $ffVersion (Gyan.dev)..."
    Invoke-WebRequest "https://github.com/GyanD/codexffmpeg/releases/download/$ffVersion/ffmpeg-$ffVersion-essentials_build.zip" -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $ffSha256) { throw 'empreinte du zip FFmpeg incorrecte' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        New-Item -ItemType Directory -Force (Split-Path $ffmpeg) | Out-Null
        foreach ($name in 'bin/ffmpeg.exe', 'LICENSE') {
            $entry = $z.Entries | Where-Object { $_.FullName -eq "ffmpeg-$ffVersion-essentials_build/$name" }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path (Split-Path $ffmpeg) (Split-Path $name -Leaf)), $true)
        }
    } finally { $z.Dispose(); Remove-Item $zip }
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
