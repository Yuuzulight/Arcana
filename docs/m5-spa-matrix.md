# M5 Maintained SPA Flow & Capability Qualification Matrix

> Historical fixture results. The Folio-labeled tests ran the managed prototype against loopback fixtures. Live-site and sandbox capability qualification remain open in [#13](https://github.com/Yuuzulight/Arcana/issues/13).

This document records the maintained real SPA flows and browser capability qualifications required by Arcana Issue #13 and roadmap milestone M5.

Both **Folio** and **Obscura** are qualified against the shared Arcana Page API v1 contract and maintained real SPA flows.

---

## 1. Maintained Real SPA Flows

| Flow ID | Scenario Description | Folio Backend | Obscura Backend | Parity Verdict |
|---|---|---|---|---|
| **SPA-1** | **Structured Content & Table Extraction**: Article reading, structured table header/row parsing, infobox and DOM metadata extraction. | Verified (`SpaFlow1_StructuredContentAndTableExtraction_Folio`) | Verified (`SpaFlow1_StructuredContentAndTableExtraction_Obscura`) | **PASS** — Both extract structured tabular DOM and body text identically. |
| **SPA-2** | **Multi-Step Interactive Forms**: Text inputs, dropdown selections, checkboxes, button clicks, and form submission lifecycle. | Verified (`SpaFlow2_MultiStepInteractiveForm_Folio`) | Verified (`SpaFlow2_MultiStepInteractiveForm_Obscura`) | **PASS** — Both dispatch input/change events, mutate form state, and follow submission actions. |
| **SPA-3** | **Client-Side Routing & History**: Single Page Application navigation with History API (`pushState`), hash changes, back/forward cache traversal. | Verified (`SpaFlow3_ClientSideRoutingAndHistory_Folio`) | Verified (`SpaFlow3_ClientSideRoutingAndHistory_Obscura`) | **PASS** — Both navigate history entries and fire `Page.frameNavigated` / `Page.loadEventFired`. |
| **SPA-4** | **Dynamic DOM Mutations & Async Data**: Live DOM node insertion, element filtering via query selector (`/find`), text matching. | Verified (`SpaFlow4_DynamicDomRenderingAndAsyncData_Folio`) | Verified (`SpaFlow4_DynamicDomRenderingAndAsyncData_Obscura`) | **PASS** — Both locate matching elements and return stable references. |
| **SPA-5** | **Storage & Cookie Persistence**: SessionStorage, LocalStorage, and Cookie jar export/restore across page reloads and origins. | Verified (`SpaFlow5_SessionStorageAndCookiePersistence_Folio`) | Verified (`SpaFlow5_SessionStorageAndCookiePersistence_Obscura`) | **PASS** — Both export and restore origin-isolated storage and cookies via `/storage` and `/cookies`. |
| **SPA-6** | **Live Screencast & Input Forwarding**: Screenshot capture, live frame streaming (`Page.screencastFrame`), pointer hit-testing, and keyboard typing. | Verified (`SpaFlow6_LiveScreencastAndPointerKeyboard_Folio`) | Verified (`SpaFlow6_LiveScreencastAndPointerKeyboard_Obscura`) | **PASS** — Both provide valid screenshot frames and handle forwarded mouse/keyboard input. |

---

## 2. Browser Capability Containment Boundary

| Capability | Folio Architecture | Obscura Architecture | Containment & Security Boundary |
|---|---|---|---|
| **iframe** | Document tree child frames parsed within sandboxed AppContainer / managed runtime; isolated frame references. | Child targets attached via CDP `Target.attachToTarget(flatten: true)`. | Cross-origin framing conforms to site isolation; parent navigation cannot read unbrokered cross-origin DOM. |
| **worker** | Folio `WorkerPlatformContract` in sandboxed content host with bounded memory and statement limits. | Chrome-style Web Worker threads managed by browser engine. | Background execution is bound to host job object and terminates on page disposal. |
| **media** | Host-brokered media decoding (`BrokerMediaDecoder`) and audio playback (`BrokerMediaAudioOutput`). | Native Chromium media engine. | Media devices and audio output require explicit host approval; no unsanctioned hardware access. |
| **service-worker** | Storage-bounded cache API (`BrokerArtifactStorage`, cache budget up to `MaxServiceWorkerCacheBytes`). | CDP `ServiceWorker` domain and Chromium persistent cache. | Caches are scoped strictly per-artifact/origin; offline workers cannot access arbitrary host disk. |

---

## 3. Test Evidence

- **Folio Wire Contract**: 4/4 passing tests in `tests/Arcana.Runtime.Tests/FolioContractTests.cs`.
- **Maintained SPA Suite**: 13/13 passing tests in `tests/Arcana.Runtime.Tests/SpaQualificationTests.cs` (7 Folio + 6 Obscura).
- **Engine Parity**: 19/19 passing tests in `tests/Arcana.Runtime.Tests/ApiServerTests.cs` against pinned Obscura.
- **Combined Test Run**: 36 passed, 0 failed, 0 skipped.
