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

## Planned agent surface

The next layer will sit above CDP and expose stable, backend-neutral operations:

1. navigate and wait for a settled page;
2. DOM snapshot with stable element references;
3. link, form, and button actions;
4. screenshots and click annotations;
5. cookies/storage scoped to a Mana-owned profile;
6. network request policy and redaction;
7. visible takeover and hand-back.

Obscura's CDP domains are the initial compatibility target. Edge/Chrome can be used as external correctness oracles during development, but they are not Arcana runtime backends.
