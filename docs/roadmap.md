# Arcana roadmap

This roadmap keeps the browsing contract ahead of the engine implementation. Obscura is the first engine behind Arcana; Folio can be added later by implementing the same contract.

## M0: Done

- Mana's headless browser runs on Arcana over CDP, with Edge as the explicit fallback.
- The engine dies with its host, including crash cleanup through the Windows job object and parent-process watch.

## M1: The API contract, and Mana off Playwright

**Status: Done.**

1. Specify and version the page API in [page-api.md](page-api.md).
2. Serve it over a loopback HTTP/WebSocket endpoint with a bearer token and origin checks.
3. Run it on Obscura.
4. Move Mana's browser session code off Playwright while retaining the Edge fallback.
5. Make the contract tests engine-independent; every engine must pass them.

Exit condition: Mana's browser tests and a live session pass with Playwright out of the Arcana path. Verified in `Mana/docs/roadmap/arcana-m1-acceptance.md` and `tests/Arcana.Runtime.Tests/ApiServerTests.cs`.

## M2: Parity on real flows, and dependability

**Status: Parity comparison complete; soak runner hardened.**

- Run Mana's real flows through the API and compare them with Edge.
- Recheck the Obscura 0.2.1 gaps, including missing links and script text.
- Measure startup, memory with one and three tabs, and page load on local hardware.
- Schedule newest-main builds, contract tests, and pin-bump pull requests.
- Add clear failure reasons and network-idle waits for single-page apps.

Exit condition: a working/different/failing table exists with an issue for every failure, followed by 72 hours without unexplained Edge fallback. Gap dispositions are documented in [m2-parity.md](m2-parity.md); soak evaluation logic in Mana #1535 requires clean fallback evidence.

## M3: Live view, approvals, annotate

**Status: Done.** Merged in [Mana #1530](https://github.com/Yuuzulight/Mana/pull/1530) and [Mana #1534](https://github.com/Yuuzulight/Mana/pull/1534).

- Drive the Browser panel from screencast events.
- Show approved actions as they happen.
- Add click-to-annotate with redaction and a fallback crop box for empty inline layout rectangles.

Exit condition: the panel receives live page frames, approved actions are visible,
and an annotated element is pinned into the next agent result. Verified by the
Mana activity/route tests and the real Arcana M4 control test.

## M4: Take over inside Arcana

**Status: Done.** Merged in [Mana #1532](https://github.com/Yuuzulight/Mana/pull/1532).

- Forward mouse, keyboard, and scroll input through the API.
- Spike a bank-style login, a CAPTCHA, and a SaaS login; offer real-browser handoff where Arcana is rejected.

Exit condition: ordinary-page manual help works, page state survives hand-back,
and all three sign-in classes have an explicit handoff decision. Verified in
`Mana/docs/roadmap/arcana-m4-acceptance.md`. Credential entry, CAPTCHA solving,
and credential persistence remain intentionally outside the agent path.

## Release signing — Postponed

Code-signing certificates and signed release artifacts are deliberately deferred.
This does not block M3/M4 functionality or the Obscura-backed soak; revisit it
before distributing a signed release installer.

## M5: The Folio engine

**Status: Open.** [Issue #13](https://github.com/Yuuzulight/Arcana/issues/13) tracks integration with the sandboxed Folio content host. The managed prototype exercises local fixtures. Qualification requires the actual Folio renderer and script engine to pass the shared API contract and maintained live-site flows.

## M6: Cutover

**Status: Open.** Obscura is the default during Folio qualification. After M5 passes, switch Mana and Arcana to Folio, retain Obscura as an explicit option for one release, and verify browser fallback and Playwright behavior in Mana.
