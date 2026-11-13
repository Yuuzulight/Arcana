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

## Remaining gap dispositions

| Issue | Target / behavior | Arcana direct status | Disposition |
| --- | --- | --- | --- |
| [#5](https://github.com/Yuuzulight/Arcana/issues/5) | Tables break snapshots (`HTMLTableElement` DOM API) | Pass | Resolved on direct API snapshot path (covered by #16 regression fixture and Alan Turing article validation). Legacy Playwright `ariaSnapshot` is bypassed by direct API. |
| [#6](https://github.com/Yuuzulight/Arcana/issues/6) | Clicks land on wrong element / pointer intercepted | Pass with fallback | Direct API element click resolves live node references; navigation links use explicit `NavigateAndWaitAsync`; pointer-intercepted targets use manual handoff / OS browser fallback. |
| [#7](https://github.com/Yuuzulight/Arcana/issues/7) | YouTube empty page (`window.__shady_native_addEventListener`) | Edge fallback | Web Components shadow DOM polyfill is missing in Obscura. Complex web-component SPAs route to explicit Edge fallback; Folio engine in M5 provides modern standards compliance. |
| [#8](https://github.com/Yuuzulight/Arcana/issues/8) | Amazon 202 "Server Busy" (bot detection) | Handoff / Fallback | Anti-bot / bot-challenge surfaces route to user handoff or OS default browser (per M4 acceptance decisions). |
| [#9](https://github.com/Yuuzulight/Arcana/issues/9) | GitHub repository page load timing | Pass (2.47s–3.70s) | Did not reproduce on direct API path across multiple fresh runs (3.70s, 2.50s, 2.47s including process startup and snapshot). Closed as resolved for direct path. |

The 72-hour unexplained-fallback exit criterion remains an operational follow-up: collect three days of unattended Mana runs after this integration branch is merged, with no unexplained Edge fallback. Clean fallback evidence is required before passing the soak.
