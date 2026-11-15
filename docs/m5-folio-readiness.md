# M5 Folio backend readiness

**Status: Qualified and verified.**

The standalone Folio content broker and worker running in the Windows AppContainer sandbox are qualified across all fourteen required integration tests against pinned Folio commit `8369feed0bd96f94bd693b806584a5a63c9ec0a8` (content broker SHA-256 `cd812337cc4e1052be83a333a91e86a939bb40cbf2840e0f6ff50e5b3b38f5cb`).

## Verified slice

| Capability | Current evidence | Boundary |
|---|---|---|
| API backend startup | `api --backend=folio` starts the real broker and worker; broker handshake confirms sandboxing. | Windows only; requires a broker-enabled `Folio.ContentHost` build. |
| Inline fixture lifecycle | `POST /v1/pages` with inline HTML at `about:blank`, page snapshot/evaluation, and `DELETE` run through the sandboxed worker. | HTML is limited to 1 MiB. An inline document uses about:blank. |
| Process cleanup | Arcana API integration test kills the Arcana host without killing descendants and confirms broker and worker exit. Folio PR #792 also covers idle and active parent-loss cleanup. | Handled via Job Object and parent process watch. |
| HTTPS navigation | Repeat `--folio-allow-origin=HTTPS_URL` to allow specific origins or path prefixes. Verified on public HTTPS fixtures, redirect handling, and policy refusal. | Without an allowed origin, the broker runs offline. |
| Screenshots | The v1 screenshot route returns the sandboxed renderer's PNG bytes. | JPEG returns HTTP 501. |
| Coordinate and reference input | Pointer click, keyboard type, select, press, and scroll forward directly to the document. | Covered by `FolioApiForwardsPointerKeyboardTextAndWheelToDocument`. |
| Storage partitioning and retention | `localStorage` and `sessionStorage` partition by origin and retain across back-navigation and reload without leaking across origins; `localStorage` persists across broker restarts when given the same profile directory. | Verified in `FolioNavigationRetainsStorageWithoutExposingItToAnotherOrigin` and `FolioStoragePersistsAcrossBrowserRestartsWithSameProfile`. |
| Release packaging | The self-contained Windows release package ships Folio as the default engine and rejects Obscura backend selection. | Verified in packaging and smoke tests. |

## Combined Windows qualification

Run from the Arcana checkout with the qualified Folio broker:

```powershell
pwsh -NoProfile -File tools/test-folio-qualification.ps1 -Broker D:/GitHub Projects/Folio/artifacts/content-host/Folio.ContentHost.exe
```

The command uses the exact single broker executable for all fourteen required Folio integration tests. All 14 tests passed:
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
- `FolioStoragePersistsAcrossBrowserRestartsWithSameProfile` (Passed)

Test report archived: `artifacts/folio-qualification/folio-aa483096ac324ea7879fa6f11371967e.trx`.

## M6 Cutover

Folio is cut over as the default engine in Arcana (`ArcanaOptions.DefaultBackend = "folio"`). The packaged release build rejects Obscura backend selection, running solely on Folio with explicit OS default browser handoff.
