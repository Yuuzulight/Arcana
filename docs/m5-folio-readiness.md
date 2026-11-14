# M5 Folio backend readiness

**Status: partial integration; qualification remains open in [#13](https://github.com/Yuuzulight/Arcana/issues/13).**

The earlier managed `FolioBrowserBackend` prototype was not a real Folio browser backend. Its tests are removed from the acceptance suite. The opt-in API path now launches Folio's `--broker` mode, which in turn starts the content worker through `ContentProcessClient.Start` and requires the Windows AppContainer sandbox.

## Verified slice

| Capability | Current evidence | Boundary |
|---|---|---|
| API backend startup | `api --backend=folio --folio-broker=PATH` starts the real broker and worker; broker handshake must confirm sandboxing. | Windows only; requires a broker-enabled `Folio.ContentHost` build. |
| Inline fixture lifecycle | `POST /v1/pages` with inline HTML at `about:blank`, page snapshot/evaluation, and `DELETE` run through the sandboxed worker. | HTML is limited to 1 MiB; no network resource loader is configured. |
| Process cleanup | Arcana API integration test kills the Arcana host without killing descendants and confirms broker and worker exit. Folio PR #792 also covers idle and active parent-loss cleanup. | Does not establish long-running service supervision or multi-page stress. |
| Unsupported operations | Navigation, actions, screenshots, cookies, storage, CDP, and event/log subscriptions return HTTP 501. | These operations must not be described as implemented. |
| Obscura contract | Existing pinned-engine contract and SPA regression tests remain enabled. | Passing Obscura tests is not Folio qualification. |

## Windows CI smoke

The Windows workflow checks out Folio at pinned commit `bbe3de574921f5741f761886d85d980a5a1abfd8`, publishes its standalone `win-x64` production host, and runs the Arcana integration tests against that executable. The gate requires an observable JavaScript DOM mutation, a rendered frame with non-background pixel evidence, and broker/worker cleanup; it validates the TRX results so missing or skipped tests fail CI. The workflow also uploads the Folio build manifest and qualification TRX. This gate covers the current offline inline-fixture slice only.

## Still required for M5

- A brokered and policy-controlled resource path for local fixtures and web navigation.
- Browser actions, screenshots, cookies/storage, and event/log support with explicit real-engine evidence.
- Shared API contract coverage and maintained SPA flows on the actual Folio backend.
- Review and merge of [Folio broker PR #792](https://github.com/Yuuzulight/Folio/pull/792), followed by exact-head verification.

The M6 default-engine cutover remains separate. Obscura stays the default until the M5 gates are met.
