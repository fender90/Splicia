# Builds the self-contained app and the installer.
#   publish\Splicia.exe + publish\ffmpeg\   the app (runs from publish\)
#   dist\Splicia-Setup-<ver>.exe             the installer: the same exe with the ffmpeg runtime appended as a zip
#                                                 (the "Setup" name switches it to the install wizard)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# FFmpeg runtime (BtbN's GPL shared build of FFmpeg 8.1, matching FFmpeg.AutoGen 8.1), fetched once into ffmpeg\
$ffFiles = 'avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swscale-9.dll', 'swresample-6.dll', 'avfilter-11.dll', 'avdevice-62.dll', 'ffmpeg.exe'
if ($ffFiles | Where-Object { -not (Test-Path "ffmpeg\$_") }) {
    Write-Host 'Downloading FFmpeg 8.1...'
    $tmp = Join-Path 'obj' "ffmpeg-download"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $tmp | Out-Null
    Invoke-WebRequest 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip' -OutFile "$tmp\ff.zip"
    Expand-Archive "$tmp\ff.zip" $tmp
    $bin = Get-ChildItem $tmp -Recurse -Directory -Filter bin | Select-Object -First 1
    New-Item -ItemType Directory -Force ffmpeg | Out-Null
    foreach ($f in $ffFiles + 'ffprobe.exe') { Copy-Item (Join-Path $bin.FullName $f) ffmpeg\ }
    Copy-Item (Join-Path $bin.Parent.FullName 'LICENSE.txt') ffmpeg\
    Remove-Item $tmp -Recurse -Force
}

dotnet publish -c Release -o publish
if ($LASTEXITCODE) { throw "publish failed" }
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# Payload: a zip of the ffmpeg runtime, appended to the exe, followed by its length (8 bytes) and the marker
# "VTPAYLD1". The installer copies the exe part and unpacks the zip into <install dir>\ffmpeg.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipPath = Join-Path $PSScriptRoot 'obj\ffmpeg-payload.zip'
Remove-Item $zipPath -ErrorAction SilentlyContinue
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($f in $ffFiles + 'LICENSE.txt') {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot "ffmpeg\$f"), $f, 'Optimal')
    }
}
finally { $zip.Dispose() }

New-Item -ItemType Directory -Force dist | Out-Null
Get-ChildItem dist -Filter 'Splicia-Setup-*.exe' | Remove-Item
$setup = Join-Path $PSScriptRoot "dist\Splicia-Setup-$version.exe"
$out = [System.IO.File]::Create($setup)
try {
    foreach ($part in (Join-Path $PSScriptRoot 'publish\Splicia.exe'), $zipPath) {
        $in = [System.IO.File]::OpenRead($part)
        try { $in.CopyTo($out) } finally { $in.Dispose() }
    }
    $len = (Get-Item $zipPath).Length
    $tail = [BitConverter]::GetBytes([long]$len) + [System.Text.Encoding]::ASCII.GetBytes('VTPAYLD1')
    $out.Write($tail, 0, $tail.Length)
}
finally { $out.Dispose() }
Remove-Item $zipPath

Get-ChildItem publish\Splicia.exe, dist\*.exe | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
