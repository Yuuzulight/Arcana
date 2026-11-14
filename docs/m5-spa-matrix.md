# M5 SPA and capability qualification status

**Folio qualification is open in [#13](https://github.com/Yuuzulight/Arcana/issues/13).**

Previous Folio-labeled SPA results used the managed prototype and are not Folio acceptance evidence. Those tests have been removed. Their shared HTML fixtures in `TestSite` remain available for real-engine qualification.

## Obscura regression coverage

The six maintained SPA flows in `SpaQualificationTests` run against pinned Obscura: structured tables, interactive forms, client-side routing, dynamic DOM, storage/cookies, and screenshots/input. These results establish Obscura behavior only.

## Real Folio backend slice

| Operation | Status |
|---|---|
| Broker startup with sandbox confirmation | Verified against the broker-enabled Folio ContentHost build. |
| Self-contained inline HTML at `about:blank` | Verified through `ContentProcessClient.Start` and the sandboxed worker. |
| Snapshot and JavaScript evaluation | Verified by the Arcana API integration test; JavaScript state changes persist in the document. |
| Page create/delete and host-exit cleanup | Verified, including broker and worker exit after killing the Arcana host alone. |
| Navigation, interactions, screenshot API, cookies/storage, CDP, events/logs | Unsupported; API returns HTTP 501. |
| Live sites, network/session access, and shared SPA parity | Not qualified. |

The Folio-owned sandbox broker and its idle/active parent-loss tests are in [PR #792](https://github.com/Yuuzulight/Folio/pull/792). These results do not close M5 or authorize M6 cutover.
