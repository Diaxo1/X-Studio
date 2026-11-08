param(
    [string]$InstallPath,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$setupFolder = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Get-ChildItem -LiteralPath $setupFolder -File -Filter 'XStudio-Release-win-x64-*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$payload) {
    [System.Windows.Forms.MessageBox]::Show('The X Studio app package is missing. Extract the full setup download, then run Install-XStudio.cmd.', 'X Studio Setup', 'OK', 'Error') | Out-Null
    exit 1
}

if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    $default = Join-Path $env:LOCALAPPDATA 'Programs\X Studio'
    $picker = New-Object System.Windows.Forms.FolderBrowserDialog
    $picker.Description = 'Choose the folder where X Studio will be installed.'
    $picker.SelectedPath = if (Test-Path -LiteralPath $default) { $default } else { Split-Path -Parent $default }
    $picker.ShowNewFolderButton = $true
    if ($picker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
    $InstallPath = if ([System.IO.Path]::GetFileName($picker.SelectedPath) -eq 'X Studio') { $picker.SelectedPath } else { Join-Path $picker.SelectedPath 'X Studio' }
}

$InstallPath = [System.IO.Path]::GetFullPath($InstallPath)
if ($InstallPath.TrimEnd('\').Length -le 3) {
    [System.Windows.Forms.MessageBox]::Show('Choose a folder for X Studio. It cannot be installed at the drive root.', 'X Studio Setup', 'OK', 'Warning') | Out-Null
    exit 1
}
if (Get-Process -Name XStudio -ErrorAction SilentlyContinue) {
    [System.Windows.Forms.MessageBox]::Show('Close X Studio before installing or updating it, then run Setup again.', 'X Studio Setup', 'OK', 'Information') | Out-Null
    exit 1
}
if ([System.Windows.Forms.MessageBox]::Show("Install X Studio to:`n`n$InstallPath`n`nThis installs the app and its bundled media tools for your Windows account.", 'Confirm installation', 'OKCancel', 'Information') -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }

try {
    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Expand-Archive -LiteralPath $payload.FullName -DestinationPath $InstallPath -Force
    Copy-Item -LiteralPath (Join-Path $setupFolder 'Uninstall-XStudio.ps1') -Destination $InstallPath -Force

    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'X Studio'
    New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $startMenu 'X Studio.lnk'))
    $shortcut.TargetPath = Join-Path $InstallPath 'XStudio.exe'
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.IconLocation = "$InstallPath\XStudio.exe,0"
    $shortcut.Save()

    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XStudio'
    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'X Studio' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '0.1.0 Alpha' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'X Studio' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $InstallPath -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value "$InstallPath\XStudio.exe" -PropertyType String -Force | Out-Null
    $powershell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $uninstaller = Join-Path $InstallPath 'Uninstall-XStudio.ps1'
    New-ItemProperty -Path $uninstallKey -Name UninstallString -Value "`"$powershell`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`"" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

    $launchNow = $Launch -or ([System.Windows.Forms.MessageBox]::Show('X Studio installed successfully. Launch it now?', 'X Studio Setup', 'YesNo', 'Information') -eq [System.Windows.Forms.DialogResult]::Yes)
    if ($launchNow) { Start-Process -FilePath (Join-Path $InstallPath 'XStudio.exe') -WorkingDirectory $InstallPath }
}
catch {
    [System.Windows.Forms.MessageBox]::Show("X Studio could not be installed.`n`n$($_.Exception.Message)", 'X Studio Setup', 'OK', 'Error') | Out-Null
    exit 1
}
