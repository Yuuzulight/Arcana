[CmdletBinding()]
param(
    [string]$Destination,
    [ValidateSet('render', 'stealth')]
    [string]$Variant = 'stealth',
    [switch]$UseRelease,
    [string]$Repository
)

$ErrorActionPreference = 'Stop'
$Destination = if ([string]::IsNullOrWhiteSpace($Destination)) {
    Join-Path $PSScriptRoot '..\engines\obscura\current'
} else {
    $Destination
}
$destination = [IO.Path]::GetFullPath($Destination)
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\engine.lock.json') -Raw | ConvertFrom-Json
$Repository = if ([string]::IsNullOrWhiteSpace($Repository)) { 'https://github.com/' + $manifest.repository + '.git' } else { $Repository }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$archive = $null
$sourceDirectory = $null
try {
    if ($manifest.sourceCommit -and -not $UseRelease) {
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is required to build the pinned Obscura source.' }
        if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw 'cargo is required to build the pinned Obscura source.' }

        $sourceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('arcana-obscura-source-' + [guid]::NewGuid().ToString('N'))
        & git clone --filter=blob:none --no-checkout $Repository $sourceDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Obscura source clone failed.' }
        & git -C $sourceDirectory fetch --depth 1 origin $manifest.sourceCommit
        if ($LASTEXITCODE -ne 0) { throw 'Pinned Obscura source fetch failed.' }
        & git -C $sourceDirectory checkout --detach $manifest.sourceCommit
        if ($LASTEXITCODE -ne 0) { throw 'Pinned Obscura source checkout failed.' }

        $features = if ($Variant -eq 'stealth') { 'render,stealth' } else { 'render' }
        & cargo build --manifest-path (Join-Path $sourceDirectory 'Cargo.toml') --release -p obscura-cli --bins --features $features
        if ($LASTEXITCODE -ne 0) { throw 'Pinned Obscura source build failed.' }

        foreach ($binary in @('obscura.exe', 'obscura-worker.exe')) {
            $built = Join-Path $sourceDirectory ('target\release\' + $binary)
            if (-not (Test-Path -LiteralPath $built)) { throw "Obscura build did not produce $binary." }
            Copy-Item -LiteralPath $built -Destination (Join-Path $destination $binary) -Force
        }
        Write-Host "Arcana engine $($manifest.version) ($Variant), source commit $($manifest.sourceCommit), installed under $destination"
    } else {
        $archive = Join-Path ([IO.Path]::GetTempPath()) ('arcana-engine-' + [guid]::NewGuid().ToString('N') + '.zip')
        $asset = $manifest.assets.$Variant
        $url = 'https://github.com/' + $manifest.repository + '/releases/download/' + $manifest.releaseVersion + '/' + $asset.name
        if (Get-Command gh -ErrorAction SilentlyContinue) {
            & gh release download $manifest.releaseVersion --repo $manifest.repository --pattern $asset.name --output $archive
            if ($LASTEXITCODE -ne 0) { throw 'Engine download failed.' }
        } else {
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
        }
        $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($digest -ne $asset.sha256) { throw "Engine checksum mismatch: $digest" }
        Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
        Write-Host "Arcana engine $($manifest.releaseVersion) ($Variant), SHA256 verified, installed under $destination"
    }
}
finally {
    if ($archive -and (Test-Path -LiteralPath $archive)) {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
    }
    if ($sourceDirectory -and (Test-Path -LiteralPath $sourceDirectory)) {
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedSource = [IO.Path]::GetFullPath($sourceDirectory)
        if (-not $resolvedSource.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolvedSource).StartsWith('arcana-obscura-source-', [StringComparison]::Ordinal))) {
            throw "Refusing to remove unexpected source path: $resolvedSource"
        }
        Remove-Item -LiteralPath $resolvedSource -Recurse -Force -ErrorAction SilentlyContinue
    }
}
