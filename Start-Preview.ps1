param(
    [switch]$NoRelay,          # A talks to B directly, no translation layer
    [string]$SmokeDir = ''     # automated run: scripted input, screenshots written here
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'Windows\MysterySort.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "Missing build: $exe" }
$logs = Join-Path $env:LOCALAPPDATA 'MysterySortDemo\Logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

$bPort = if ($NoRelay) { 9000 } else { 9002 }
$common = @('-screen-width','1000','-screen-height','800','-screen-fullscreen','0')
$smoke = if ($SmokeDir) { @('-smokeDir', ('"' + $SmokeDir + '"')) } else { @() }

if (!$NoRelay) {
    $python = Join-Path $env:USERPROFILE 'anaconda3\python.exe'
    if (!(Test-Path -LiteralPath $python)) { $python = 'python' }
    $relay = Join-Path $PSScriptRoot 'relay\relay.py'
    $relayArgs = @(('"' + $relay + '"'), '--listen', '9000', '--b-port', '9002', '--local')
    if ($SmokeDir) { $relayArgs += @('--log', ('"' + (Join-Path $SmokeDir 'relay-log.jsonl') + '"')) }
    Start-Process -FilePath $python -ArgumentList $relayArgs
    Start-Sleep -Milliseconds 800
}
Start-Process -FilePath $exe -ArgumentList (@('-role','B','-port',$bPort,'-logFile',('"' + (Join-Path $logs 'B.log') + '"')) + $common + $smoke)
Start-Sleep -Milliseconds 800
Start-Process -FilePath $exe -ArgumentList (@('-role','A','-peer','127.0.0.1','-port','9000','-logFile',('"' + (Join-Path $logs 'A.log') + '"')) + $common + $smoke)
