<#
.SYNOPSIS
  Registers two scheduled tasks:
    ClaudeCodeMonitor-PowerOn  - at logon or resume from sleep: turn the clock on
    ClaudeCodeMonitor-PowerOff - on shutdown/restart initiated (event 1074) or entering sleep (event 42): turn the clock off
  Re-run to update. Remove with: Unregister-ScheduledTask ClaudeCodeMonitor-PowerOn,ClaudeCodeMonitor-PowerOff
#>
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Set-AwtrixPower.ps1'
$ps = 'powershell.exe'
function Act($state, $attempts) {
    New-ScheduledTaskAction -Execute $ps -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`" -State $state -Attempts $attempts"
}
function EventTrigger($query) {
    $t = New-CimInstance -CimClass (Get-CimClass MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler) -ClientOnly
    $t.Enabled = $true
    $t.Subscription = "<QueryList><Query Id=`"0`" Path=`"System`"><Select Path=`"System`">$query</Select></Query></QueryList>"
    $t
}
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -StartWhenAvailable

# ON at logon and at resume from sleep (Kernel-Power event 107). Retry up to ~1 min while Wi-Fi comes up.
$onTriggers = @(
    (New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME),
    (EventTrigger "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107]]")
)
Register-ScheduledTask -TaskName 'ClaudeCodeMonitor-PowerOn' -Action (Act 'On' 20) -Trigger $onTriggers -Settings $settings -Force | Out-Null

# OFF when shutdown/restart is initiated (User32 event 1074) or the PC enters sleep (Kernel-Power event 42).
# Must be quick: 2 attempts only.
$offTriggers = @(
    (EventTrigger "*[System[Provider[@Name='User32'] and EventID=1074]]"),
    (EventTrigger "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=42]]")
)
Register-ScheduledTask -TaskName 'ClaudeCodeMonitor-PowerOff' -Action (Act 'Off' 2) -Trigger $offTriggers -Settings $settings -Force | Out-Null

Get-ScheduledTask 'ClaudeCodeMonitor-Power*' | Select-Object TaskName, State
