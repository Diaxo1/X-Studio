param(
    [Parameter(Mandatory = $true)]
    [string]$PackageZip
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$packageZip = (Resolve-Path -LiteralPath $PackageZip).Path
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$staging = Join-Path $projectRoot "dist\installer-staging-$stamp"
$setupZip = Join-Path $projectRoot "dist\XStudio-Setup-win-x64-$stamp.zip"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item -LiteralPath $packageZip -Destination $staging
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\Install-XStudio.ps1') -Destination $staging
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\Uninstall-XStudio.ps1') -Destination $staging
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $staging

$launcher = @'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-XStudio.ps1" %*
if errorlevel 1 pause
'@
Set-Content -LiteralPath (Join-Path $staging 'Install-XStudio.cmd') -Value $launcher -Encoding ascii
Set-Content -LiteralPath (Join-Path $staging 'README.txt') -Value @'
X STUDIO WINDOWS INSTALLER

Extract this setup ZIP, then double-click Install-XStudio.cmd.
The setup asks where to install X Studio and adds a Start menu shortcut and an uninstall entry for your Windows account.
It installs the app and bundled runtime tools. It does not require administrator access or a .NET runtime on the target PC.
Your scenes and destinations are kept under your Windows profile if you uninstall the app.

The alpha installer is unsigned, so Windows may show an unknown-publisher notice.
See THIRD-PARTY-NOTICES.md for bundled component licenses.
'@ -Encoding utf8

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $setupZip -CompressionLevel Optimal
$digest = (Get-FileHash -Algorithm SHA256 -LiteralPath $setupZip).Hash
Set-Content -LiteralPath "$setupZip.sha256" -Value "$digest  $(Split-Path -Leaf $setupZip)" -Encoding ascii
Get-Item -LiteralPath $setupZip | Select-Object FullName, Length
Write-Host "SHA-256 $digest"
