[CmdletBinding()]
param(
    [string]$Destination,
    [ValidateSet('render', 'stealth')]
    [string]$Variant = 'stealth'
)

$ErrorActionPreference = 'Stop'
$Destination = if ([string]::IsNullOrWhiteSpace($Destination)) {
    Join-Path $PSScriptRoot '..\engines\obscura\current'
} else {
    $Destination
}
$destination = [IO.Path]::GetFullPath($Destination)
$archive = Join-Path ([IO.Path]::GetTempPath()) ('arcana-engine-' + [guid]::NewGuid().ToString('N') + '.zip')
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\engine.lock.json') -Raw | ConvertFrom-Json
$asset = $manifest.assets.$Variant
$url = 'https://github.com/' + $manifest.repository + '/releases/download/' + $manifest.version + '/' + $asset.name

New-Item -ItemType Directory -Force -Path $destination | Out-Null
try {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        # gh supports an exact output path and handles GitHub's release redirects.
        & gh release download $manifest.version --repo $manifest.repository --pattern $asset.name --output $archive
        if ($LASTEXITCODE -ne 0) { throw 'Engine download failed.' }
    } else {
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    }
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($digest -ne $asset.sha256) { throw "Engine checksum mismatch: $digest" }
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
    Write-Host "Arcana engine $($manifest.version) ($Variant), SHA256 verified, installed under $destination"
}
finally {
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
}
