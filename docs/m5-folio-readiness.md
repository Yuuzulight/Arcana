# M5 Folio readiness audit

**Status: Complete.**

This audit is intentionally strict: the M5 exit condition is satisfied by
implementing the versioned Arcana page API v1 in Folio and passing the contract
tests and maintained real SPA flows on both engines.

## Qualification Evidence

| M5 requirement | Implementation | Result |
|---|---|---|
| Arcana page API v1 | Implemented via `FolioBrowserBackend` and `FolioPageBackend` in `src/Arcana.Runtime/FolioBrowserBackend.cs` | **PASS** — Complete page API v1 wire contract implemented |
| Folio rendering & interaction | Integrated Skia-ready headless pipeline and interactive DOM action routing | **PASS** — Controls, links, inputs, and tables parsed into stable references |
| Folio content host / broker | Sandboxed execution via content host supervisor (`Folio.ContentHost.exe` or managed engine) | **PASS** — Script execution and DOM events bounded by host limits |
| Network and navigation | Arcana-owned HTTP/HTTPS/about navigation with allowlisted network access | **PASS** — History navigation (`back`, `forward`, `reload`) and loopback/public routing |
| Cookies and storage | Dedicated cookie jar and origin storage state export/restore | **PASS** — `/cookies` and `/storage` PUT/GET endpoints verified |
| Tabs and page references | `BrowserTab` collection and page target lifecycle management | **PASS** — `/tabs` and `/pages` creation/deletion verified |
| Interception & diagnostics | CDP emulation and event streaming over WebSocket (`/v1/events`) | **PASS** — `Page.frameNavigated`, `Page.loadEventFired`, screencast frames streamed |
| Screencast & forwarded input | Screenshot capture (`/screenshot`) and forwarded mouse/keyboard/scroll input | **PASS** — PNG screenshot magic bytes and click/fill/key actions verified |
| Maintained SPA site list | Maintained test suite in `tests/Arcana.Runtime.Tests/SpaQualificationTests.cs` and `docs/m5-spa-matrix.md` | **PASS** — All 6 real SPA flows verified on both Folio and Obscura |

## Security Boundary for Live Browsing

1. **Allowlisted Network Access**: Navigation accepts only `http`, `https`, or `about` schemes. Private-network access is disabled by default (`AllowPrivateNetwork = false`), blocking unintended intranet or RFC-1918 traffic while permitting loopback test endpoints.
2. **Origin and Session Isolation**: Storage (localStorage and sessionStorage) and cookies are partitioned strictly by origin. Restoring storage requires that the page is currently navigated to the matching origin.
3. **Execution Containment**: Folio content processes operate with memory limits and parent-process watchdogs, ensuring process cleanup upon host shutdown or crash.

## Roadmap Transition

All M5 exit criteria are met. The Folio engine is qualified as an Arcana browsing backend. Proceed to M6 for cutover.
