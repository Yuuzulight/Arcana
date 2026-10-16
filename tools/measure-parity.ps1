param(
  [Parameter(Mandatory = $true)][string]$Executable,
  [int]$TabCount = 3
)

$ErrorActionPreference = 'Stop'
$token = -join ((1..32) | ForEach-Object { '{0:x}' -f (Get-Random -Minimum 0 -Maximum 16) })
$stdout = Join-Path ([IO.Path]::GetTempPath()) ("arcana-measure-" + [guid]::NewGuid() + '.out')
$stderr = Join-Path ([IO.Path]::GetTempPath()) ("arcana-measure-" + [guid]::NewGuid() + '.err')
$arguments = if ($Executable -match '\.dll$') { @($Executable, 'api', ('--token=' + $token), '--port=0') } else { @('api', ('--token=' + $token), '--port=0') }
$file = if ($Executable -match '\.dll$') { 'dotnet' } else { $Executable }
$timer = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $file -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
try {
  $ready = $null
  while (-not $ready -and $timer.Elapsed.TotalSeconds -lt 20) {
    if ($process.HasExited) { throw "Arcana exited: $(Get-Content $stderr -Raw)" }
    if (Test-Path $stdout) { foreach ($line in Get-Content $stdout) { try { $ready = $line | ConvertFrom-Json } catch {} } }
    Start-Sleep -Milliseconds 50
  }
  if (-not $ready) { throw 'Arcana did not publish an API endpoint within 20 seconds.' }
  $startupMs = [math]::Round($timer.Elapsed.TotalMilliseconds)
  $headers = @{ Authorization = 'Bearer ' + $ready.token }
  $base = $ready.endpoint.TrimEnd('/')
  $healthTimer = [Diagnostics.Stopwatch]::StartNew(); Invoke-RestMethod ($base + '/v1/health') -Headers $headers | Out-Null; $healthTimer.Stop()
  $pages = @()
  $pageTimer = [Diagnostics.Stopwatch]::StartNew()
  for ($i = 0; $i -lt $TabCount; $i++) { $pages += (Invoke-RestMethod ($base + '/v1/pages') -Method Post -Headers $headers -ContentType 'application/json' -Body '{}').pageId }
  $pageTimer.Stop()
  [pscustomobject]@{
    executable = $Executable
    startupMs = $startupMs
    healthMs = [math]::Round($healthTimer.Elapsed.TotalMilliseconds)
    tabs = $TabCount
    createTabsMs = [math]::Round($pageTimer.Elapsed.TotalMilliseconds)
    workingSetMb = [math]::Round($process.WorkingSet64 / 1MB, 1)
  } | ConvertTo-Json
}
finally {
  if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
  Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
}
