$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$installPath = [System.IO.Path]::GetFullPath($PSScriptRoot)
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XStudio'
$registeredPath = (Get-ItemProperty -LiteralPath $uninstallKey -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
if (!$registeredPath -or [System.IO.Path]::GetFullPath($registeredPath) -ne $installPath -or !(Test-Path -LiteralPath (Join-Path $installPath 'XStudio.exe'))) {
    [System.Windows.Forms.MessageBox]::Show('The installation location could not be verified, so no files were removed.', 'X Studio Uninstall', 'OK', 'Warning') | Out-Null
    exit 1
}
if (Get-Process -Name XStudio -ErrorAction SilentlyContinue) {
    [System.Windows.Forms.MessageBox]::Show('Close X Studio before uninstalling it, then try again.', 'X Studio Uninstall', 'OK', 'Information') | Out-Null
    exit 1
}
if ([System.Windows.Forms.MessageBox]::Show('Remove X Studio from this PC?', 'X Studio Uninstall', 'YesNo', 'Question') -ne [System.Windows.Forms.DialogResult]::Yes) { exit 0 }

$programs = [Environment]::GetFolderPath('Programs')
$startMenu = Join-Path $programs 'X Studio'
Remove-Item -LiteralPath (Join-Path $startMenu 'X Studio.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $startMenu -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue

try {
    Remove-Item -LiteralPath $installPath -Recurse -Force
    [System.Windows.Forms.MessageBox]::Show('X Studio has been removed. Your saved scenes and destinations remain in your local profile.', 'X Studio Uninstall', 'OK', 'Information') | Out-Null
}
catch {
    [System.Windows.Forms.MessageBox]::Show("X Studio could not remove all files.`n`n$($_.Exception.Message)", 'X Studio Uninstall', 'OK', 'Warning') | Out-Null
    exit 1
}
