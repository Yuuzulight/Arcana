# M5 Folio backend readiness

**Status: partial integration; qualification remains open in [#13](https://github.com/Yuuzulight/Arcana/issues/13).**

The earlier managed `FolioBrowserBackend` prototype was not a real Folio browser backend. Its tests are removed from the acceptance suite. The opt-in API path now launches Folio's `--broker` mode, which in turn starts the content worker through `ContentProcessClient.Start` and requires the Windows AppContainer sandbox.

## Verified slice

| Capability | Current evidence | Boundary |
|---|---|---|
| API backend startup | `api --backend=folio --folio-broker=PATH` starts the real broker and worker; broker handshake must confirm sandboxing. | Windows only; requires a broker-enabled `Folio.ContentHost` build. |
| Inline fixture lifecycle | `POST /v1/pages` with inline HTML at `about:blank`, page snapshot/evaluation, and `DELETE` run through the sandboxed worker. | HTML is limited to 1 MiB. An inline document uses about:blank. |
| Process cleanup | Arcana API integration test kills the Arcana host without killing descendants and confirms broker and worker exit. Folio PR #792 also covers idle and active parent-loss cleanup. | Does not establish long-running service supervision or multi-page stress. |
| HTTPS navigation | Repeat `--folio-allow-origin=HTTPS_URL` to allow specific origins or path prefixes. Local Windows tests covered final redirect URLs, relative scripts, and policy refusal. | Without an allowed origin, the broker runs offline. |
| Screenshots | The v1 screenshot route returns the sandboxed renderer's PNG bytes. | JPEG returns HTTP 501. |
| Unsupported operations | Page actions, cookies, storage, CDP, and event/log subscriptions return HTTP 501 on this branch. | The input adapter is tracked in [#30](https://github.com/Yuuzulight/Arcana/pull/30). |
| Obscura contract | Existing pinned-engine contract and SPA regression tests remain enabled. | Passing Obscura tests is not Folio qualification. |

## Still required for M5

- Browser actions, cookies/storage, and event/log support with explicit real-engine evidence.
- Merge and hosted verification of the brokered navigation and screenshot paths.
- Shared API contract coverage and maintained SPA flows on the actual Folio backend.
- Review and merge of [Folio broker PR #792](https://github.com/Yuuzulight/Folio/pull/792), followed by exact-head verification.

The M6 default-engine cutover remains separate. Obscura stays the default until the M5 gates are met.
