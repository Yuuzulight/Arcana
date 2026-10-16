# Arcana roadmap

This roadmap keeps the browsing contract ahead of the engine implementation. Obscura is the first engine behind Arcana; Folio can be added later by implementing the same contract.

## M0: Done

- Mana's headless browser runs on Arcana over CDP, with Edge as the explicit fallback.
- The engine dies with its host, including crash cleanup through the Windows job object and parent-process watch.

## M1: The API contract, and Mana off Playwright

1. Specify and version the page API in [page-api.md](page-api.md).
2. Serve it over a loopback HTTP/WebSocket endpoint with a bearer token and origin checks.
3. Run it on Obscura.
4. Move Mana's browser session code off Playwright while retaining the Edge fallback.
5. Make the contract tests engine-independent; every engine must pass them.

Exit condition: Mana's browser tests and a live session pass with Playwright out of the Arcana path.

## M2: Parity on real flows, and dependability

- Run Mana's real flows through the API and compare them with Edge.
- Recheck the Obscura 0.2.1 gaps, including missing links and script text.
- Measure startup, memory with one and three tabs, and page load on local hardware.
- Schedule newest-main builds, contract tests, and pin-bump pull requests.
- Add clear failure reasons and network-idle waits for single-page apps.

Exit condition: a working/different/failing table exists with an issue for every failure, followed by a week without unexplained Edge fallback.

## M3: Live view, approvals, annotate

- Drive the Browser panel from screencast events.
- Show approved actions as they happen.
- Add click-to-annotate with redaction and a fallback crop box for empty inline layout rectangles.

## M4: Take over inside Arcana

- Forward mouse, keyboard, and scroll input through the API.
- Spike a bank-style login, a CAPTCHA, and a SaaS login; offer real-browser handoff where Arcana is rejected.

## M5: The Folio engine

Folio implements the M1 contract after its JavaScript, iframe, worker, media, and service-worker milestones. The contract tests and a maintained SPA site list are the checkpoint.

## M6: Cutover

Swap Obscura for Folio in one release, keep Obscura as a build option for one version, then record the decision about Edge and Playwright in Mana's roadmap.
