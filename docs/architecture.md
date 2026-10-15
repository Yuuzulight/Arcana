# Arcana Architecture

Arcana is Mana's browser runtime and the boundary between Mana and its Obscura-derived rendering engine.

## Backend boundary

`BrowserRuntime` owns one short-lived Arcana engine process and exposes its loopback CDP endpoint. The first implementation uses:

- **Arcana engine:** the primary browser, derived from the Obscura engine and exposed through Arcana's policy boundary;
- **Default-browser fallback:** Windows' registered URL handler, used only after an explicit fallback decision. On this machine that is Opera GX.

The runtime does not silently switch to Edge or Chrome. Mana records the Arcana failure and the fallback URL, then opens the user's default browser only when the caller explicitly requests it. This keeps permissions and benchmark results honest.

## Resource policy

Every runtime starts with a Mana-owned profile unless the host supplies a deliberate profile directory. All endpoints bind to loopback, and private-network access is disabled unless the host opts in.

`BrowserRuntime.DisposeAsync` kills the complete child process tree and removes temporary profiles where Windows permits it. Future versions will add job-object enforcement so cleanup still happens if the host is terminated abruptly.

## Agent page surface

`ArcanaPage.SnapshotAsync` returns the URL, title, and up to 500 controls with references, names, roles, link destinations, input types, and disabled state. It excludes hidden/inert controls and does not serialize input values. References use a per-page store and per-document identity, so repeated snapshots preserve identity while document replacement and detached nodes invalidate old references.

`ClickAsync` and `FillAsync` validate references against the current document and reject hidden or disabled targets. Fill accepts text inputs, textareas, and editable content and emits input/change events. These are synthetic DOM interactions, not trusted hardware events. JavaScript exceptions and navigation errors propagate to callers.

Visibility uses DOM/CSS checks rather than layout rectangles because the installed Obscura v0.2.4 engine can return empty rectangles for inline links. This does not guarantee that a control is on screen or unobstructed. Iframes, shadow roots, application readiness, native input, and takeover remain future work.

Remaining layers will extend this surface with:

1. navigate and wait for a settled page;
2. DOM snapshot with stable element references;
3. link, form, and button actions;
4. screenshots and click annotations;
5. cookies/storage scoped to a Mana-owned profile;
6. network request policy and redaction;
7. visible takeover and hand-back.

Obscura's CDP domains are the initial compatibility target. Edge/Chrome can be used as external correctness oracles during development, but they are not Arcana runtime backends.
