# M2 real-flow parity record

This is the M2 evidence table for the pinned Obscura engine and Mana direct API path. Rows are updated from the local fixture suite and the measurement script; Edge comparisons are recorded when a behavior differs.

| Flow or property | Arcana direct API | Edge reference | Status / evidence |
| --- | --- | --- | --- |
| Search, read, forms, paging | Pass | Pass | Mana browser suite and Arcana fixture tests pass. |
| Persistent cookies across restart | Pass | Pass | Profile `credentials.json` round-trip test passes. |
| Upload | Pass | Pass | Real Mana upload test passes. |
| Download approval | Pass in Mana layer | Pass | Mana download policy tests pass; engine download event forwarding remains part of the event contract. |
| Console and network diagnostics | Pass | Pass | `/logs` is bounded and tested; direct adapter consumes `/v1/events`. |
| Request interception / ad blocking | Pass | Pass | Direct adapter enables Fetch interception and maps route abort/continue. |
| SPA readiness | Pass | Pass | Versioned `networkidle` wait uses a 500 ms quiet window and is tested against Obscura. |
| Obscura 0.2.1 links and script text | Pass | Pass | Existing native parity tests cover links, text, and evaluated scripts. |

The local comparison fixture (`tools/compare-engines.ps1`) was run against the pinned Obscura build and the installed Edge executable. Both engines returned the fixture title, `/next` link, and `script-visible` marker. Arcana page creation/load measured **203 ms** and Edge headless dump-dom measured **1,033 ms** on this host; these are smoke measurements, not a general browser benchmark.

## Local measurement

Run `tools/measure-parity.ps1` with the locally built CLI to record startup, health, three-tab creation, and working-set measurements on the host running the test. A representative run on 2026-10-16 was:

```json
{"startupMs":362,"healthMs":120,"tabs":3,"createTabsMs":35,"workingSetMb":43.2}
```

The scheduled workflow [obscura-pin-bump.yml](../.github/workflows/obscura-pin-bump.yml) resolves the newest Obscura `main`, updates `engine.lock.json`, and opens a pin-bump pull request whose checks run the contract suite.

The one-week unexplained-fallback exit criterion remains an operational follow-up: collect seven days of Mana runs after this integration branch is merged, with no unexplained Edge fallback.
