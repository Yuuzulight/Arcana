param(
    [Parameter(Mandatory)][string]$Broker,
    [string]$Results = 'artifacts/folio-qualification'
)

$ErrorActionPreference = 'Stop'
$brokerPath = (Resolve-Path -LiteralPath $Broker).Path
$resultsPath = [IO.Path]::GetFullPath($Results)
$repo = Split-Path -Parent $PSScriptRoot
$trxPath = Join-Path $resultsPath ("folio-" + [Guid]::NewGuid().ToString('N') + ".trx")
$settings = @{
    ARCANA_TEST_FOLIO_BROKER = $brokerPath
    ARCANA_TEST_FOLIO_NAVIGATION_BROKER = $brokerPath
    ARCANA_TEST_FOLIO_INPUT_BROKER = $brokerPath
    ARCANA_TEST_ARCANA_CLI = Join-Path $repo 'src/Arcana.Cli/bin/Release/net10.0/Arcana.Cli.dll'
    ARCANA_TEST_FOLIO_LIVE_WEB = '1'
    ARCANA_TEST_FOLIO_HTTPS_FIXTURE = 'https://httpbingo.org/redirect-to?url=%2Fhtml'
    ARCANA_TEST_FOLIO_HTTPS_FINAL_URL = 'https://httpbingo.org/html'
}
$previous = @{}
try {
    foreach ($key in $settings.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $settings[$key])
    }
    $hash = (Get-FileHash -LiteralPath $brokerPath).Hash
    $arcanaCommit = git -C $repo rev-parse HEAD
    if ($LASTEXITCODE) { throw 'Cannot identify the Arcana source revision.' }
    New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null
    [ordered]@{
        arcanaCommit = $arcanaCommit
        brokerSha256 = $hash.ToLowerInvariant()
        report = [IO.Path]::GetFileName($trxPath)
    } | ConvertTo-Json | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($trxPath, '.json'))
    Write-Output "Folio broker SHA-256 $hash"
    dotnet build (Join-Path $repo 'Arcana.slnx') -c Release
    if ($LASTEXITCODE) { throw 'Arcana Release build failed.' }
    dotnet test (Join-Path $repo 'Arcana.slnx') -c Release --no-build --filter 'FullyQualifiedName~FolioApiBackendTests' --results-directory $resultsPath --logger "trx;LogFileName=$([IO.Path]::GetFileName($trxPath))"
    if ($LASTEXITCODE) { throw "Folio qualification failed. Report: $trxPath" }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $required = @(
        'FolioApiUsesSandboxedInlineDocumentAndRejectsUnsupportedOperations'
        'FolioNavigationRefusesAnUnlistedOriginAndRetiresThePage'
        'FolioNavigationLoadsTheAllowedHttpsDocument'
        'FolioNavigationRefusesARedirectOutsideTheAllowedPath'
        'FolioNavigationRunsARelativeExternalScript'
        'ArcanaCliDoesNotReportReadyWhenFolioDisablesTheSandbox'
        'FolioApiForwardsPointerKeyboardTextAndWheelToDocument'
        'FolioBrokerRunsDomMutationAndProducesARealRenderedFrame'
        'ArcanaHostExitReleasesBrokerAndSandboxWorker'
    )
    $actual = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    foreach ($name in $required) {
        $matches = @($actual | Where-Object { $_.testName -eq $name })
        if ($matches.Count -ne 1 -or $matches[0].outcome -ne 'Passed') {
            throw "Expected one passing result for $name. Report: $trxPath"
        }
    }
    if ($actual.Count -ne $required.Count) { throw "Unexpected test count. Report: $trxPath" }
    if ((Get-FileHash -LiteralPath $brokerPath).Hash -ne $hash) { throw 'Folio broker changed during qualification.' }
    Write-Output "Passed all $($required.Count) required tests. Report: $trxPath"
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
}
