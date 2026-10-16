# Arcana roadmap

Arcana is a browser built for agents and, from the start, a product in its own right, with Mana as its first user. Today it hosts
upstream Obscura. The end state is Arcana's own engine, built on [Folio](https://github.com/Yuuzulight/Folio), able to run modern
single-page apps. Each phase has an exit test; a phase is done when that test passes on real flows.

## Decisions

| Question | Decision |
| --- | --- |
| End state | Arcana's own renderer. Obscura is the engine until then. |
| Renderer base | Folio, in its own repo. Arcana consumes it as a package. Folio stays offline and sandboxed; Arcana adds the browsing layer. |
| Success bar | Modern single-page apps work. |
| Switching engines | One cutover, when Folio's engine is ready. Mana stays on Obscura meanwhile. |
| What clients talk to | Arcana's own page API, not CDP or Playwright. Obscura and Folio both sit behind it. |
| Transport | A loopback HTTP/WebSocket server with a token. Events (screencast, console, network) stream over the socket. |
| Sign-in, CAPTCHA, payment | Handled inside Arcana eventually. Until it can, and whenever a site rejects it, hand off to a real browser. |
| Audience | A product from the start: documented, versioned API. Mana's real tasks decide priorities. |
| Safety | Mechanisms live in Arcana (interception, blocklists, sensitive-page detection, loopback, token and origin checks, private network off, resource limits). Policy lives in Mana (per-site permission, approvals, game and RAM gates, ad-host list). |

## Done

- Mana's browser plugin runs on Arcana's `serve` endpoint over CDP (Mana#1489); Edge is the fallback and Take over browser.
- The engine dies with its host, crash included: job object and `--arcana-parent-pid` (Arcana#2, Mana#1493).

## 1. The API contract, and Mana off Playwright

Everything later depends on this, so it comes first.

- Specify the page API: navigate, snapshot with stable refs, click, type, select, press, scroll, back, find, screenshot, tabs,
  cookies and storage, console and network logs, request interception, screencast, upload and download events. Version it and
  document it.
- Serve it over loopback HTTP and WebSocket with a token; reject foreign origins.
- Run it on Obscura. Move Mana's session code (`plugins/browser-automation`) from Playwright onto it. Keep the Edge fallback.
- Contract tests that any engine must pass; they are the bar the Folio engine later has to clear.
- Exit: Mana's browser tests and a live session pass with Playwright out of the Arcana path.

## 2. Parity on real flows, and dependability

- Run Mana's real flows through the API and compare with Edge: search and read, form fill and submit, paging, login that persists
  across restarts, upload and download, `test_site`, `look_and_click`, devtools, ad blocking.
- Recheck the gaps found on Obscura 0.2.1 (Mana#508): missing links, script text in page text.
- Measure startup, memory with one and three tabs, and page load on Mana's hardware.
- A scheduled job that builds the newest Obscura `main`, runs the contract tests and opens a pin-bump PR.
- Clear reasons when it fails, and network-idle waits for single-page apps.
- Exit: a table of what works, differs and fails, with an issue per failure, then a week of use with no unexplained Edge fallback.

## 3. Live view, approvals, annotate

- Browser panel from the screencast event instead of screenshot polling (Mana#418).
- Her actions shown as they happen, through Mana's approval gate.
- Click-to-annotate (Mana#647): selector, markup, role and name, with password and hidden values redacted. Obscura can return empty
  layout rectangles for inline elements, so crops need a fallback box.
- Exit: the panel shows the page moving while she works, and a pinned element reaches her next turn with its data.

## 4. Take over inside Arcana

- Forward mouse, keyboard and scroll from the panel through the API (Mana's `manual-control.js` already does this for Chromium).
- Spike three real sign-in pages first (a bank-style login, a CAPTCHA, a SaaS login). Where a site rejects Arcana, offer the real-browser handoff.
- Exit: manual help works on ordinary pages; the spike result says which sign-ins Arcana can take.

## 5. The Folio engine

The long phase, and the only one without a date. It must not hold up phases 1 to 4, which are useful on Obscura.

- Depends on Folio's own JavaScript engine, now Jint as a stand-in, and on Folio's later milestones (iframes, workers, media, service workers).
- Arcana's browsing layer on Folio: network stack, navigation, cookies and storage, sessions, form submission, input, the security sandbox for live sites.
- Built against the phase 1 contract tests, so it can be checked while Mana still runs on Obscura.
- Checkpoint: a list of single-page-app sites Mana uses, tested on both engines. The Folio engine is ready when it passes that list.
- Exit: the contract tests and the site list both pass.

## 6. Cutover

Swap Obscura for the Folio engine in one release, with Obscura kept as a build option for a version. Then decide the fate of Edge and
the Playwright dependency. Exit: the decision, recorded in Mana's roadmap.

## Open questions

- What exactly goes in the page API's first version, and how are stable refs defined so they survive an engine swap?
- A licence and release plan for outside users: versioning, binaries, support promises.
- How much of Folio's later milestones is needed for the site list, versus nice to have.
