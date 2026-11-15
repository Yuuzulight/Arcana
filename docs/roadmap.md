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

**Status: Obscura parity comparison documented. Folio release-candidate parity remains open.**

- Run Mana's real flows through the API and compare them with Edge.
- Recheck the Obscura 0.2.1 gaps, including missing links and script text.
- Measure startup, memory with one and three tabs, and page load on local hardware.
- Schedule newest-main builds, contract tests, and pin-bump pull requests.
- Add clear failure reasons and network-idle waits for single-page apps.

Exit condition: a working/different/failing table exists with an issue for every failure. Gap dispositions are documented in [m2-parity.md](m2-parity.md). The 72-hour unattended soak is outside the Arcana 1.0 release criteria.

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
Revisit signing before distributing a signed release installer.

## M5: The Folio engine

**Status: Open.** The local single-broker suite passes 13 tests against Folio revision `d47fe5907e608a293bf49afbbc50abb790ad0071`. It covers sandboxing, input, allowlisted HTTPS navigation, rendered screenshots, lifecycle cleanup, and origin-scoped storage. Hosted qualification, native event delivery, and real flow parity remain open.

## M6: Cutover, packaging, and publication

**Status: Open.** Ship Folio as the only bundled 1.0 engine. Verify Mana's normal browser path over the Page API and explicit handoff to the OS default browser. Validate the final Windows package and publish it with SHA-256 checksums. Code signing is postponed.
