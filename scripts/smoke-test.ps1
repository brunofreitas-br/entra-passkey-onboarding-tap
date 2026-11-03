param([string]$BaseUrl = 'http://localhost:8080')
$ErrorActionPreference = 'Stop'
$live = Invoke-RestMethod "$BaseUrl/health/live"
if ($live.status -ne 'healthy') { throw 'Liveness check failed.' }
$ready = Invoke-RestMethod "$BaseUrl/health/ready"
if ($ready.status -ne 'healthy') { throw 'Readiness check failed.' }
Write-Host "Smoke tests passed for $BaseUrl"
