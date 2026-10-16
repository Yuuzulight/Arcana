# Arcana roadmap

Goal: Arcana is the only browser Mana needs. Today it is her headless browser; Edge remains for Take over and as a fallback.
Each phase has an exit test, and a phase is done when that test passes on real flows, not when the code exists.

## Done

- Mana's browser plugin runs on Arcana's `serve` endpoint over CDP (Mana#1489); Edge is fallback and Take over.
- Engine dies with its host, on a crash too: job object and `--arcana-parent-pid` (Arcana#2, Mana#1493).

## 1. Prove parity on Mana's real flows

Unit parity (`docs/parity.md`) tests Arcana against Obscura. Nobody has yet tested Arcana against what Mana actually does.
Run each flow on Arcana and on Edge, same prompts, and list the differences.

- Flows: search and read, form fill and submit, paging, login that must persist across restarts (cookies, not only
  localStorage in `--storage-dir`), upload and download, `test_site`, `look_and_click`, devtools console/network, ad blocking.
- Recheck the two gaps found on Obscura 0.2.1 (Mana#508): links missing from the element list, `<script>` text in page text.
- Measure on Mana's hardware: startup, RSS with one and three tabs, page-load, against Edge.
- Exit: a written table of what works, what differs, what fails; every "fails" has an issue.

## 2. Make it dependable

- A scheduled check that builds the newest Obscura `main`, runs the suite, and opens a pin-bump PR when green.
- Failures reach Mana as a clear reason ("Arcana isn't built", "engine exited"), not a silent fallback to Edge.
- Wait helpers for single-page apps (network idle), since `readyState=complete` is not enough.
- Exit: a week of Mana use with no unexplained Edge fallback.

## 3. Live view and approvals

- Use `Page.startScreencast` (already verified in the parity suite) for the Browser panel instead of screenshot polling.
- Show her actions as they happen (Mana#418) and route clicks/typing through Mana's approval gate.
- Exit: the panel shows the page moving while she works, with no extra screenshots taken.

## 4. Click-to-annotate (Mana#647)

- Pins from the DOM snapshot: selector, markup, role and name, with password and hidden values redacted.
- Known limit: Obscura can return empty layout rectangles for inline elements, so cropped screenshots need a fallback box.
- Exit: the user points at an element in the panel and her next turn carries the note and the element's data.

## 5. Take over inside Arcana

The largest piece, and the one that decides whether Edge can go.

- Mana already forwards mouse, keyboard and scroll from the panel to the page (`manual-control.js`); extend it to Arcana via CDP input.
- Risk to settle first: login, CAPTCHA and payment pages often reject non-Chromium engines. Spike three real sign-in pages
  (a bank-style login, a CAPTCHA, a SaaS login) before designing anything.
- Exit: the spike result. If sign-in works, build it; if not, Edge stays as the Take over browser and this phase is closed
  as "not worth it".

## 6. Retire Edge and Playwright

Only after 1 to 5. Remove the Edge launch path and the Playwright dependency, or keep Edge as the documented
Take over browser if phase 5 said so. Exit: the decision, recorded in Mana's roadmap.
