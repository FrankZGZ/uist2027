# Stops any running relay for this demo (use before starting a new one, or before the desktop preview).
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like '*MysterySortDemo*relay.py*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; "stopped relay (pid $($_.ProcessId))" }
