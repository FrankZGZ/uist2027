# Starts the relay for two standalone Quests. Keep this window open during the session.
$ErrorActionPreference = 'Stop'
$python = Join-Path $env:USERPROFILE 'anaconda3\python.exe'
if (!(Test-Path -LiteralPath $python)) { $python = 'python' }
Write-Host 'This PC''s IPv4 addresses (the headsets must be on the same Wi-Fi network):'
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    ForEach-Object { Write-Host ('  ' + $_.IPAddress + '   (' + $_.InterfaceAlias + ')') }
Write-Host ''
& $python (Join-Path $PSScriptRoot 'relay\relay.py') --listen 9000
