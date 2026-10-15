# Arcana architecture

Arcana has three layers:

1. A pinned upstream Obscura binary pair supplies the browser engine, native CLI, MCP server, and CDP server.
2. Arcana's .NET host owns discovery, temporary profiles, process cleanup, command stdio, and the CDP client.
3. Mana-facing page helpers add snapshots, references, forms, capture, session state, and input. Mana UI integration builds on those APIs.

## Native engine boundary

`EngineCommand` forwards the complete engine argument list using `ProcessStartInfo.ArgumentList`. It relays stdin/stdout/stderr as bytes and preserves exit codes. That keeps binary downloads and MCP protocol framing intact. `fetch`, `scrape`, `serve`, and `mcp` use the same implementation as the pinned engine; `engine` supports global flags before the subcommand. Caller-supplied native settings retain upstream semantics.

`BrowserRuntime` instead supplies a managed configuration: loopback, explicit network opt-in, workers/connections, proxy, user agent, stealth, and an owned profile. It drains engine output pipes to prevent logging deadlocks. It waits for process termination before removing its temporary profile. An explicitly supplied profile is preserved.

`EngineLocator` accepts an explicit path or `ARCANA_ENGINE`, then searches installed engine directories and PATH. It deliberately searches for `arcana-engine` and `obscura`, not the Arcana CLI itself. The installer pins a version and SHA-256 in `engine.lock.json`; the engine is a separate download, not committed source.

## CDP connection

One receiver task owns WebSocket reads. Request IDs route replies to independent pending tasks. A semaphore serializes WebSocket writes without blocking on a command's response, permitting interception replies during awaited JavaScript. Commands have a 30-second default transport deadline and support caller cancellation. A canceled command's late response is discarded by ID, not mistaken for the next command's result.

Subscriptions filter by method and attached session. Each subscriber has a bounded channel; overflow closes that subscription with an explicit error without blocking other commands or silently losing events. Peer failure completes pending operations. Disposal cancels the receiver and aborts the socket instead of waiting indefinitely for a close handshake. Incoming messages are bounded to 128 MiB.

Both browser and page expose `SendCdpAsync`; this preserves access to domains without a dedicated .NET convenience wrapper. The native `serve` endpoint is also available directly to Puppeteer, Playwright, or other CDP clients.

## Page helpers

The .NET snapshot provides URL, title, readable text, and controls with document-scoped references. Input values are omitted. Weak identity mapping keeps references stable across snapshots; detached nodes and replacement documents invalidate them. Snapshots cap controls/text and mark truncation.

DOM click/fill/select/check helpers validate live targets. Coordinate and keyboard helpers use the engine's CDP input domain. Neither API operates Windows desktop applications. CSS/DOM visibility is used because Obscura v0.2.4 can return empty layout rectangles for inline links; it does not establish that an element is unobstructed.

Other helpers cover navigation/history, waits, Markdown/text/links/forms/query, viewport/full-page PNG, raster PDF, cookies, current-origin storage, tabs, and contexts. Exported storage can contain authentication secrets. Restore checks the current origin before replacing local/session storage and merging cookies according to engine semantics.

## Fallback and remaining integration

Arcana remains primary. `ArcanaFallback.OpenDefaultBrowser` delegates a validated HTTP(S) URL to Windows' registered handler only when explicitly called. It never selects or manages Edge, Chrome, or Opera by name.

Mana approvals, preview/takeover UI, and annotation are host features still to be integrated. Abrupt-host-exit cleanup requires Windows job objects; normal exit/cancellation currently performs deterministic process cleanup. See [the parity matrix](parity.md) for verified capabilities, native delegation, and remaining limits.
