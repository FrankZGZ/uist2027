param([Parameter(Mandatory=$true)][ValidateSet("A","B","MR-A","MR-B")][string]$Role, [string]$Serial)
$ErrorActionPreference = 'Stop'
$adbPath = 'C:\Program Files\Unity\Hub\Editor\6000.0.46f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
if (!(Test-Path -LiteralPath $adbPath)) {
    $adbCommand = Get-Command adb -ErrorAction SilentlyContinue
    if (!$adbCommand) { throw 'ADB not found. Install Unity Android Build Support or Android platform-tools.' }
    $adbPath = $adbCommand.Source
}
$apk = Join-Path $PSScriptRoot ('MysterySort-' + $Role + '.apk')
if (!(Test-Path -LiteralPath $apk)) { throw "APK missing: $apk" }
$rows = @(& $adbPath devices)
$devices = @($rows | Where-Object { $_ -match '^\S+\s+device$' } | ForEach-Object { ($_ -split '\s+')[0] })
if (!$Serial) {
    if ($devices.Count -ne 1) {
        $rows | Write-Host
        throw 'Connect exactly ONE Quest by USB, enable developer mode, and accept USB debugging inside the headset. Then run this installer again.'
    }
    $Serial = $devices[0]
}
if ($Serial -notin $devices) { throw 'Selected headset is not connected or USB debugging is not authorized.' }
Write-Host "Installing Mystery Sort $Role on Quest $Serial ..."
& $adbPath -s $Serial install -r $apk
if ($LASTEXITCODE -ne 0) { throw 'APK installation failed; see ADB message above.' }
Write-Host "Installed: Mystery Sort $Role. Unplug USB and launch it from Unknown Sources in the headset."
