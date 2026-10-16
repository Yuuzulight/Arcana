param(
  [Parameter(Mandatory = $true)][string]$ArcanaCli,
  [Parameter(Mandatory = $true)][string]$ArcanaEngine,
  [string]$Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('arcana-parity-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $root | Out-Null
@'
<!doctype html><html><head><title>M2 parity fixture</title><script>window.__fixtureScriptText = 'script-visible';</script></head><body>
<h1>Parity fixture</h1><a href="/next">Next page</a><form><label>Name<input name="name"></label><button>Send</button></form>
</body></html>
'@ | Set-Content (Join-Path $root 'index.html')
$server = Start-Process python -ArgumentList @('-m','http.server','0','--bind','127.0.0.1','--directory',$root) -RedirectStandardError (Join-Path $root 'server.err') -PassThru -WindowStyle Hidden
$apiOut = Join-Path $root 'api.out'; $apiErr = Join-Path $root 'api.err'
$token = -join ((1..32) | ForEach-Object { '{0:x}' -f (Get-Random -Minimum 0 -Maximum 16) })
$api = Start-Process dotnet -ArgumentList @($ArcanaCli,'api',('--executable=' + $ArcanaEngine),('--token=' + $token),'--port=0','--allow-private-network') -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -PassThru -WindowStyle Hidden
try {
  $port = $null
  while (-not $port -and -not $api.HasExited) {
    if (Test-Path $apiOut) { foreach ($line in Get-Content $apiOut) { try { $ready = $line | ConvertFrom-Json; $port = $ready.endpoint; break } catch {} } }
    Start-Sleep -Milliseconds 50
  }
  if (-not $port) { throw "Arcana API failed: $(Get-Content $apiErr -Raw)" }
  $listener = (Get-Content (Join-Path $root 'server.err') -ErrorAction SilentlyContinue | Select-String -Pattern 'port (\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
  if (-not $listener) { $listener = (Get-NetTCPConnection -OwningProcess $server.Id -State Listen).LocalPort }
  $url = "http://127.0.0.1:$listener/index.html"
  $headers = @{ Authorization = 'Bearer ' + $token }
  $timer = [Diagnostics.Stopwatch]::StartNew(); $created = Invoke-RestMethod ($port + 'v1/pages') -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ url = $url } | ConvertTo-Json); $timer.Stop()
  $snapshot = Invoke-RestMethod ($port + 'v1/pages/' + $created.pageId + '/snapshot') -Headers $headers
  $scriptValue = Invoke-RestMethod ($port + 'v1/pages/' + $created.pageId + '/evaluate') -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ expression = 'window.__fixtureScriptText' } | ConvertTo-Json)
  $linksValue = Invoke-RestMethod ($port + 'v1/pages/' + $created.pageId + '/evaluate') -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ expression = 'Array.from(document.querySelectorAll("a")).map(a => a.href)' } | ConvertTo-Json)
  $edgeOut = Join-Path $root 'edge.out'; $edgeTimer = [Diagnostics.Stopwatch]::StartNew(); $edgeProcess = Start-Process $Edge -ArgumentList @('--headless','--disable-gpu','--no-sandbox','--dump-dom',$url) -RedirectStandardOutput $edgeOut -RedirectStandardError (Join-Path $root 'edge.err') -PassThru -WindowStyle Hidden; $edgeProcess.WaitForExit(); $edgeTimer.Stop()
  [pscustomobject]@{
    url = $url
    arcana = @{ loadMs = [math]::Round($timer.Elapsed.TotalMilliseconds); title = $snapshot.title; links = @($linksValue); scriptText = $scriptValue }
    edge = @{ loadMs = [math]::Round($edgeTimer.Elapsed.TotalMilliseconds); containsTitle = (Get-Content $edgeOut -Raw).Contains('M2 parity fixture'); containsLink = (Get-Content $edgeOut -Raw).Contains('/next'); containsScriptText = (Get-Content $edgeOut -Raw).Contains('script-visible') }
  } | ConvertTo-Json -Depth 5
}
finally {
  if ($api -and -not $api.HasExited) { Stop-Process $api.Id -Force }
  if ($server -and -not $server.HasExited) { Stop-Process $server.Id -Force }
  Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
