# M5 Folio readiness audit

**Status: Open. M5 is not complete.**

This audit is intentionally strict: the M5 exit condition is not satisfied by
Folio's artifact-rendering or scripting conformance results. Arcana M5 requires
Folio to implement the versioned Arcana page API and to pass the same contract
tests as the Obscura backend.

## Current evidence

| M5 requirement | Current state | Result |
|---|---|---|
| Arcana page API v1 | Implemented and exercised against Obscura | Available as the engine-neutral contract |
| Folio static rendering and interaction | Implemented for self-contained artifacts | Not a browsing backend |
| Folio scripting/content host | Implemented in bounded, host-approved documents | Not a page/session/navigation implementation |
| Folio network and navigation | Folio's documented design keeps navigation in the host and disables network by default | Missing for Arcana |
| Folio cookies, origin storage and persistent sessions | Artifact-scoped storage exists, but Arcana's page/session semantics are not implemented | Missing for Arcana |
| Folio tabs/pages and stable page references | No Arcana-compatible Folio adapter exists | Missing |
| Upload/download, interception and diagnostics | No adapter or transport exposing the Arcana events exists | Missing |
| Screencast and forwarded input | Folio can render host controls, but does not expose Arcana's live page event stream | Missing |
| Maintained SPA site list | No Arcana/Folio checkpoint list exists | Missing |

## Required implementation before M5 can close

1. Add a Folio-backed Arcana engine adapter implementing page API v1, including
   page lifecycle, navigation, snapshots, actions, tabs, cookies, storage,
   logs, uploads/downloads, interception, screencast and input events.
2. Define the security boundary for live-site navigation: allowlisted network
   access, origin/session isolation, cookie policy, download policy and the
   content-process limits required by Arcana.
3. Run the existing engine-independent Arcana contract tests against Folio;
   failures must be fixed or explicitly documented with a routed fallback.
4. Add and maintain a SPA site list covering the real Mana flows, and run it
   against both Obscura and Folio.
5. Record runtime evidence for the complete suite and only then change the M5
   status in `docs/roadmap.md`.

## Non-claims

Folio's internal milestone-4/5 conformance results do not prove Arcana M5:
they cover scripted artifacts and bounded host capabilities, not a browser
engine that can replace Obscura for live sites. Arcana therefore remains on
Obscura, with Edge as the explicit fallback, until the requirements above pass.
