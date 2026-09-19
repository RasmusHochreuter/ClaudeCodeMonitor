<#
.SYNOPSIS
  Turns the Ulanzi TC001 (AWTRIX3) matrix on or off via POST /api/power.
  Reads the host from the first settings file that sets Monitor:AwtrixHost: appsettings.Local.json, then
  appsettings.json, looking in ../../publish before the repo root.
  Retries for a while so it survives Wi-Fi/network not being up yet at logon.
#>
param(
    [Parameter(Mandatory)][ValidateSet('On','Off')] [string]$State,
    [int]$Attempts = 20,
    [int]$DelaySeconds = 3
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$awtrixHost = @("$root\publish\appsettings.Local.json", "$root\publish\appsettings.json", "$root\appsettings.Local.json", "$root\appsettings.json") |
    Where-Object { Test-Path $_ } |
    ForEach-Object { (Get-Content $_ -Raw | ConvertFrom-Json).Monitor.AwtrixHost } |
    Where-Object { $_ } |
    Select-Object -First 1
if (-not $awtrixHost) { Write-Warning "Monitor:AwtrixHost is not set in any appsettings file"; exit 1 }
$body = '{"power":' + ($(if ($State -eq 'On') { 'true' } else { 'false' })) + '}'

for ($i = 1; $i -le $Attempts; $i++) {
    try {
        Invoke-RestMethod -Method Post -Uri "http://$awtrixHost/api/power" -ContentType 'application/json' -Body $body -TimeoutSec 3 | Out-Null
        Write-Host "AWTRIX power $State ok ($awtrixHost)"
        exit 0
    } catch {
        if ($i -eq $Attempts) { Write-Warning "AWTRIX power $State failed after $Attempts attempts: $($_.Exception.Message)"; exit 1 }
        Start-Sleep -Seconds $DelaySeconds
    }
}
