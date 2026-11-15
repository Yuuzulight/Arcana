[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FolioPublish,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$FolioCommit,
    [Parameter(Mandatory)][string]$OutputZip
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Build the Windows x64 package on Windows.' }
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputZip)
$checksum = "$output.sha256"
if ([IO.Path]::GetExtension($output) -ne '.zip' -or (Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $checksum)) {
    throw 'Provide an unused .zip output path.'
}
$folio = (Resolve-Path -LiteralPath $FolioPublish).Path
$manifest = Get-Content -LiteralPath (Join-Path $folio 'native-build.json') -Raw | ConvertFrom-Json
if ($manifest.sourceCommit -cne $FolioCommit -or $manifest.sourceDirty -ne $false -or $manifest.runtime -cne 'win-x64') {
    throw 'Use a clean win-x64 native Folio publish from the pinned revision.'
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if (!$Expected -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "Checksum mismatch for $Path."
    }
}

Assert-Hash (Join-Path $folio 'Folio.ContentHost.exe') $manifest.executableSha256
$fonts = Join-Path $folio 'fonts'
$fontManifest = Join-Path $fonts 'SHA256SUMS'
Assert-Hash $fontManifest $manifest.fontManifestSha256
$fontPins = @(Get-Content -LiteralPath $fontManifest)
if ($fontPins.Count -eq 0) { throw 'The Folio font manifest is empty.' }
foreach ($pin in $fontPins) {
    if ($pin -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9_.-]+\.(?:ttf|otf))$') {
        throw "Invalid Folio font manifest entry: $pin"
    }
    Assert-Hash (Join-Path $fonts $Matches[2]) $Matches[1]
}

$arcanaCommit = git -C $repo rev-parse HEAD
if ($LASTEXITCODE) { throw 'Could not identify the Arcana source revision.' }
$changed = git -C $repo status --porcelain --untracked-files=no
if ($LASTEXITCODE -or $changed) { throw 'Package Arcana from a clean source checkout.' }

$stage = [IO.Directory]::CreateTempSubdirectory('arcana-package-').FullName
try {
    $package = Join-Path $stage 'package'
    dotnet publish (Join-Path $repo 'src/Arcana.Cli/Arcana.Cli.csproj') -c Release -r win-x64 --self-contained true -o $package
    if ($LASTEXITCODE) { throw 'Arcana publish failed.' }

    $engineParent = Join-Path $package 'engines/folio'
    New-Item -ItemType Directory -Path $engineParent | Out-Null
    Copy-Item -LiteralPath $folio -Destination (Join-Path $engineParent 'current') -Recurse
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'), (Join-Path $repo 'NOTICE') -Destination $package
    [ordered]@{
        arcanaCommit = $arcanaCommit
        folioCommit = $FolioCommit
        folioBrokerSha256 = $manifest.executableSha256
        runtime = 'win-x64'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'arcana-build.json')

    $archive = Join-Path $stage 'Arcana.zip'
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    [IO.File]::Copy($archive, $output, $false)
    "$hash  $([IO.Path]::GetFileName($output))" | Set-Content -LiteralPath $checksum -Encoding ascii
    Write-Output "Packaged Arcana $arcanaCommit with Folio $FolioCommit at $output"
} finally {
    [IO.Directory]::Delete($stage, $true)
}
