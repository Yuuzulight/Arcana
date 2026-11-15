# M5 SPA and capability qualification status

**Folio qualification status: Closed and verified against single broker build.**

All thirteen required Folio qualification integration tests pass against pinned Folio commit `d47fe5907e608a293bf49afbbc50abb790ad0071` (content broker SHA-256 `6e68d0224a282c49651d01023770a4f5b0a1cf22fcea230ba7a09f660fcb5911`), closing [#13](https://github.com/Yuuzulight/Arcana/issues/13) and [#35](https://github.com/Yuuzulight/Arcana/issues/35).

## Real Folio backend matrix

| Operation | Status | Verification evidence |
|---|---|---|
| Broker startup with sandbox confirmation | Pass | `FolioApiUsesSandboxedInlineDocumentAndRejectsUnsupportedOperations`, `ArcanaCliDoesNotReportReadyWhenFolioDisablesTheSandbox` |
| Self-contained inline HTML at `about:blank` | Pass | `ContentProcessClient.Start` and AppContainer worker verification |
| Snapshot and JavaScript evaluation | Pass | Arcana API integration tests; DOM mutations and JavaScript state persist |
| Page create/delete and host-exit cleanup | Pass | `FolioConnectionClosesAfterBrokerProcessExits`, `FolioApiDeletesTheTabAfterItsBrokerExits`, `ArcanaHostExitReleasesBrokerAndSandboxWorker` |
| HTTPS navigation & redirects | Pass | `FolioNavigationLoadsTheAllowedHttpsDocument`, `FolioNavigationRefusesARedirectOutsideTheAllowedPath`, `FolioNavigationRunsARelativeExternalScript`, `FolioNavigationRefusesAnUnlistedOriginAndRetiresThePage` |
| Rendered frame & screenshot API | Pass | `FolioBrokerRunsDomMutationAndProducesARealRenderedFrame` (PNG screenshot verification) |
| Coordinate & reference input | Pass | `FolioApiForwardsPointerKeyboardTextAndWheelToDocument` (click, type, select, press, wheel) |
| Storage retention and origin isolation | Pass | `FolioNavigationRetainsStorageWithoutExposingItToAnotherOrigin`; origin-scoped memory partitioning and Arcana profile persistence |
| Canceled request handling | Pass | `FolioPageRemainsUsableAfterAPreCanceledRequest` |
| Mana integration & real flows | Pass | Mana `arcana-real-engine.test.js` against the packaged release (actions, storage restart, live screencast frames, M4 manual control, a file upload, a download, a drag, a 110-row table and form controls; the sign-in spikes run only with `MANA_ARCANA_SIGNIN_SPIKES=1`) |

## Obscura compatibility regression coverage

Obscura remains supported as an optional compatibility build and runtime backend (`--backend=obscura`). The six maintained SPA flows in `SpaQualificationTests` remain verified against pinned Obscura, ensuring backwards compatibility for the transitional release.
