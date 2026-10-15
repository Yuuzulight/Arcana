[CmdletBinding()]
param(
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$Destination = if ([string]::IsNullOrWhiteSpace($Destination)) {
    Join-Path $PSScriptRoot '..\engines\obscura'
} else {
    $Destination
}
$destination = [IO.Path]::GetFullPath($Destination)
$archive = Join-Path ([IO.Path]::GetTempPath()) 'arcana-obscura-engine.zip'
$url = 'https://github.com/h4ckf0r0day/obscura/releases/latest/download/obscura-x86_64-windows.zip'

New-Item -ItemType Directory -Force -Path $destination | Out-Null
try {
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
    Write-Host "Arcana engine files installed under $destination"
}
finally {
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
}
