# M5 Folio backend readiness

**Status: Qualified and verified.**

The standalone Folio content broker and worker running in the Windows AppContainer sandbox are qualified across all thirteen required integration tests against pinned Folio commit `d47fe5907e608a293bf49afbbc50abb790ad0071` (content broker SHA-256 `6e68d0224a282c49651d01023770a4f5b0a1cf22fcea230ba7a09f660fcb5911`).

## Verified slice

| Capability | Current evidence | Boundary |
|---|---|---|
| API backend startup | `api --backend=folio` starts the real broker and worker; broker handshake confirms sandboxing. | Windows only; requires a broker-enabled `Folio.ContentHost` build. |
| Inline fixture lifecycle | `POST /v1/pages` with inline HTML at `about:blank`, page snapshot/evaluation, and `DELETE` run through the sandboxed worker. | HTML is limited to 1 MiB. An inline document uses about:blank. |
| Process cleanup | Arcana API integration test kills the Arcana host without killing descendants and confirms broker and worker exit. Folio PR #792 also covers idle and active parent-loss cleanup. | Handled via Job Object and parent process watch. |
| HTTPS navigation | Repeat `--folio-allow-origin=HTTPS_URL` to allow specific origins or path prefixes. Verified on public HTTPS fixtures, redirect handling, and policy refusal. | Without an allowed origin, the broker runs offline. |
| Screenshots | The v1 screenshot route returns the sandboxed renderer's PNG bytes. | JPEG returns HTTP 501. |
| Coordinate and reference input | Pointer click, keyboard type, select, press, and scroll forward directly to the document. | Covered by `FolioApiForwardsPointerKeyboardTextAndWheelToDocument`. |
| Storage partitioning and retention | `localStorage` and `sessionStorage` partition by origin and retain across back-navigation and reload without leaking across origins. | Verified in `FolioNavigationRetainsStorageWithoutExposingItToAnotherOrigin`. |
| Obscura compatibility | Obscura remains available as an explicit compatibility build option (`--backend=obscura`). | Verified in `ArcanaOptionsTests`. |

## Combined Windows qualification

Run from the Arcana checkout with the qualified Folio broker:

```powershell
pwsh -NoProfile -File tools/test-folio-qualification.ps1 -Broker D:/GitHub Projects/Folio/artifacts/content-host/Folio.ContentHost.exe
```

The command uses the exact single broker executable for all thirteen required Folio integration tests. All 13 tests passed:
- `FolioApiUsesSandboxedInlineDocumentAndRejectsUnsupportedOperations` (Passed)
- `FolioPageRemainsUsableAfterAPreCanceledRequest` (Passed)
- `FolioConnectionClosesAfterBrokerProcessExits` (Passed)
- `FolioApiDeletesTheTabAfterItsBrokerExits` (Passed)
- `FolioNavigationRefusesAnUnlistedOriginAndRetiresThePage` (Passed)
- `FolioNavigationLoadsTheAllowedHttpsDocument` (Passed)
- `FolioNavigationRefusesARedirectOutsideTheAllowedPath` (Passed)
- `FolioNavigationRunsARelativeExternalScript` (Passed)
- `ArcanaCliDoesNotReportReadyWhenFolioDisablesTheSandbox` (Passed)
- `FolioApiForwardsPointerKeyboardTextAndWheelToDocument` (Passed)
- `FolioBrokerRunsDomMutationAndProducesARealRenderedFrame` (Passed)
- `ArcanaHostExitReleasesBrokerAndSandboxWorker` (Passed)
- `FolioNavigationRetainsStorageWithoutExposingItToAnotherOrigin` (Passed)

Test report archived: `artifacts/folio-qualification/folio-3bdf80d5b32e4852969dbfcd5c2ca4b1.trx`.

## M6 Cutover

Folio is cut over as the default engine in Arcana (`ArcanaOptions.DefaultBackend = "folio"`). Obscura is retained as an explicit build and runtime option (`--backend=obscura`) for one release.
