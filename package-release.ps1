param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$toolsRoot = Join-Path $projectRoot "tools"
$packageRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) "AppData\Local\Microsoft\WinGet\Packages"

function Find-PackageFile([string]$pattern, [string]$filename) {
    $package = Get-ChildItem -LiteralPath $packageRoot -Directory -Filter $pattern | Sort-Object Name -Descending | Select-Object -First 1
    if ($null -eq $package) { return $null }
    Get-ChildItem -LiteralPath $package.FullName -Recurse -File -Filter $filename | Select-Object -First 1 -ExpandProperty FullName
}

function Install-WinGetPackage([string]$id) {
    & winget install --id $id --source winget --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "WinGet could not install $id (exit $LASTEXITCODE)." }
}

if (!(Test-Path (Join-Path $toolsRoot "uxplay\uxplay-windows.exe"))) {
    $zip = Join-Path $toolsRoot "uxplay-windows.zip"
    New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
    Invoke-WebRequest -Uri "https://github.com/leapbtw/uxplay-windows/releases/download/2.0.0.1736/uxplay-windows.zip" -OutFile $zip
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
    if ($hash -ne "9D3A51C15FC9DB857351195E7EB7BBB21700D9AE25D936A54BCF8536B62CCA18") { throw "UxPlay release checksum mismatch." }
    $uxplayFolder = Join-Path $toolsRoot "uxplay"
    New-Item -ItemType Directory -Force -Path $uxplayFolder | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $uxplayFolder -Force
    Remove-Item -LiteralPath $zip -Force
}

if (!(Test-Path (Join-Path $toolsRoot "scrcpy\scrcpy.exe"))) {
    if (!(Find-PackageFile "Genymobile.scrcpy*" "scrcpy.exe")) { Install-WinGetPackage "Genymobile.scrcpy" }
    $scrcpyExe = Find-PackageFile "Genymobile.scrcpy*" "scrcpy.exe"
    if (!$scrcpyExe) { throw "scrcpy.exe was not found after installation." }
    $source = Split-Path -Parent $scrcpyExe
    New-Item -ItemType Directory -Force -Path (Join-Path $toolsRoot "scrcpy") | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination (Join-Path $toolsRoot "scrcpy") -Recurse -Force
}

if (!(Test-Path (Join-Path $toolsRoot "mediamtx\mediamtx.exe"))) {
    if (!(Find-PackageFile "bluenviron.mediamtx*" "mediamtx.exe")) { Install-WinGetPackage "bluenviron.mediamtx" }
    $mediamtxExe = Find-PackageFile "bluenviron.mediamtx*" "mediamtx.exe"
    if (!$mediamtxExe) { throw "mediamtx.exe was not found after installation." }
    $source = Split-Path -Parent $mediamtxExe
    New-Item -ItemType Directory -Force -Path (Join-Path $toolsRoot "mediamtx") | Out-Null
    Copy-Item -LiteralPath $mediamtxExe -Destination (Join-Path $toolsRoot "mediamtx") -Force
    $license = Join-Path $source "LICENSE"
    if (Test-Path $license) { Copy-Item -LiteralPath $license -Destination (Join-Path $toolsRoot "mediamtx") -Force }
}

if (!(Test-Path (Join-Path $toolsRoot "ffmpeg\ffmpeg.exe"))) {
    if (!(Find-PackageFile "Gyan.FFmpeg*" "ffmpeg.exe")) { Install-WinGetPackage "Gyan.FFmpeg" }
    $ffmpegExe = Find-PackageFile "Gyan.FFmpeg*" "ffmpeg.exe"
    if (!$ffmpegExe) { throw "ffmpeg.exe was not found after installation." }
    New-Item -ItemType Directory -Force -Path (Join-Path $toolsRoot "ffmpeg") | Out-Null
    Copy-Item -LiteralPath $ffmpegExe -Destination (Join-Path $toolsRoot "ffmpeg") -Force
}

$required = @(
    "tools\uxplay\uxplay-windows.exe", "tools\uxplay\LICENSE.rtf",
    "tools\scrcpy\scrcpy.exe", "tools\scrcpy\adb.exe", "tools\scrcpy\LICENSE.txt",
    "tools\mediamtx\mediamtx.exe", "tools\mediamtx\LICENSE",
    "tools\ffmpeg\ffmpeg.exe", "THIRD-PARTY-NOTICES.md"
)
foreach ($relative in $required) {
    if (!(Test-Path (Join-Path $projectRoot $relative))) { throw "Missing runtime asset: $relative" }
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (!$dotnet -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
if (!$dotnet) { throw "Install the .NET 10 SDK to build X Studio." }

$stamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
$publish = Join-Path $projectRoot "dist\XStudio-$Runtime-$stamp"
$archive = Join-Path $projectRoot "dist\XStudio-$Configuration-$Runtime-$stamp.zip"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archive) | Out-Null
& $dotnet publish (Join-Path $projectRoot "desktop\desktop.csproj") -c $Configuration -r $Runtime --self-contained true -p:RestoreSources="https://api.nuget.org/v3/index.json" -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $archive -CompressionLevel Optimal
$digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
Set-Content -LiteralPath "$archive.sha256" -Value "$digest  $(Split-Path -Leaf $archive)" -Encoding ascii
Get-Item $archive | Select-Object FullName,Length
Write-Host "SHA-256 $digest"
