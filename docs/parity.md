# Obscura parity

## Target and meaning

Arcana exposes the complete native CLI, MCP, and CDP surface of the pinned **Obscura main revision** (`030a0e14d143cb9b1788855b3fe1dee7e1b8cc70`). The default installer checks out that exact source commit and builds the rendering-and-stealth variant. `-Variant render` selects the ordinary rendering build instead. `-UseRelease` selects the retained v0.2.4 archive fallback when a source build is unavailable.

Arcana currently **uses the upstream Obscura binaries**. It adds a .NET host, owned process/profile lifecycle, page API, event transport, and Mana integration boundary. This is access to the same engine's capabilities, not a separately implemented renderer or a claim of Chromium compatibility beyond Obscura's own support. The native MCP server intentionally reports its upstream name/version and retains its original tool schemas. Arcana and native MCP element references belong to different sessions and must not be exchanged.

Baseline references:

- [Pinned CLI reference](https://github.com/h4ckf0r0day/obscura/blob/030a0e14d143cb9b1788855b3fe1dee7e1b8cc70/docs/CLI-reference.md)
- [Pinned MCP implementation and tool schemas](https://github.com/h4ckf0r0day/obscura/blob/030a0e14d143cb9b1788855b3fe1dee7e1b8cc70/crates/obscura-mcp/src/lib.rs)
- [Pinned CDP domains](https://github.com/h4ckf0r0day/obscura/tree/030a0e14d143cb9b1788855b3fe1dee7e1b8cc70/crates/obscura-cdp/src/domains)
- [Release artifacts](https://github.com/h4ckf0r0day/obscura/releases/tag/v0.2.4)

## Capability matrix

| Capability | Arcana access | Verification |
| --- | --- | --- |
| Fetch HTML, text, links, Markdown, assets, cookies | `fetch`, unchanged engine arguments | Output compared byte for byte with native engine on a local fixture |
| Binary resources | `fetch --dump original` | Non-UTF-8 bytes, NULs, and line endings preserved exactly |
| Parallel scraping | `scrape`, including worker process | Two fixture URLs with concurrent workers and evaluated titles |
| CDP server / Puppeteer / Playwright endpoint | `serve`, or `BrowserRuntime` | HTTP version endpoint; .NET protocol integration against the engine; external Puppeteer/Playwright suites not run |
| Versioned page API v1 | `api` CLI command and `ArcanaApiServer` | Authenticated loopback health, page creation, snapshot, navigation, deletion, and origin rejection tests |
| All 37 MCP browser tools | `mcp` over stdio | Exact tool schema comparison; every advertised tool called successfully on a fixture |
| HTTP MCP | `mcp --http` | Initialize via authenticated HTTP; missing bearer rejected |
| Navigation, back, forward, reload | MCP and .NET page methods | Titles/history checked after navigation |
| Text, links, forms, structured extraction, search | MCP; .NET snapshots, text, Markdown, links, forms, query | MCP calls plus assertions on .NET fixture content |
| Fill, type, click, select, checkbox, batch forms | MCP; .NET page methods | Values, event delivery, coordinate click, and real POST submission checked |
| Wait for selector/text/condition | MCP and .NET methods | Existing content and timeout failure tested |
| Cookies and storage state | MCP and .NET export/restore | Round trip, clear, origin guard, context isolation, same-origin navigation persistence |
| Tabs / browser contexts | MCP and .NET browser methods | List, attach, close, switch; isolated cookie jars |
| Viewport and full-page PNG, raster PDF | MCP and .NET capture methods | PNG/PDF signatures; native full-page capture exercised |
| Live screencast | CDP events, `Page.startScreencast` / ack / stop | Frame received, decoded, acknowledged, stopped |
| Network and console diagnostics | MCP; filtered .NET event subscriptions | Console message and network response observed |
| Live request interception | `SendCdpAsync` plus subscriptions | JS fetch fulfilled while evaluation is waiting for its response |
| Response-body streaming | Native CDP `Fetch` / `IO` through .NET | Cached body streamed, content verified, stream closed |
| Other CDP domains / settings | `SendCdpAsync`, native endpoint | Delegated without an Arcana method allowlist; errors retained |
| Proxy, user agent, stealth, robots, V8 flags, persistence, batch URLs | Native engine arguments; common settings also in `ArcanaOptions` | Native implementation retained; TLS fingerprinting, real proxies, and every flag combination not independently tested |
| Default private-network protection | Native engine; explicit managed opt-in | CLI denies localhost without opt-in; fixtures run with explicit opt-in |
| Transport reliability | Concurrent CDP receiver and filtered event queues | Out-of-order responses, cancellation/late reply, command errors, peer close, queue overflow |

The regression suite verifies Arcana's integration and exposed surface. It does not run the entire Rust/Web Platform Test suite, benchmark every website, or establish an independent rendering conformance score. Upstream renderer and Web API limitations remain. New features on Obscura's `main` branch are not automatically part of this pinned baseline.

## Run the suite

```powershell
./tools/fetch-engine.ps1
$env:ARCANA_TEST_ENGINE = (Resolve-Path engines/obscura/current/obscura.exe).Path
dotnet test Arcana.slnx --logger 'trx;LogFileName=parity.trx'
```

Without `ARCANA_TEST_ENGINE`, engine integration cases are explicitly skipped. CI installs the pinned build and runs them on Windows. Fixture traffic stays on loopback; no public website is needed. Timing is not used as an assertion.

## Arcana-specific limits

- The .NET snapshot helper covers the top document, up to 500 controls and 12,000 characters of readable text, with truncation flags. It omits input values. Its stable references expire when the document changes or the element detaches.
- `.NET ClickAsync`/`FillAsync` use DOM events. Coordinate mouse/keyboard helpers use the engine's CDP input implementation. These do not operate the Windows desktop or create an interactive browser window.
- Snapshot visibility uses CSS/DOM state and does not guarantee hit-test visibility. Use coordinate input when visual targeting matters.
- `.NET NavigateAndWaitAsync` waits for `readyState=complete`; it does not promise SPA/network-idle readiness. Use explicit conditions or the native engine's wait modes.
- The convenience viewport method bounds dimensions to 8192 CSS pixels; use native CDP for the engine's complete parameter surface. CDP messages are capped at 128 MiB. Subscribers fail explicitly on overflow instead of silently dropping events.
- `.NET ExportStorageAsync` exports the current origin plus its context's cookie jar; restore requires the page to be on that origin. The pinned engine keeps `localStorage` across same-origin navigation, scopes it to the browser context, keeps `sessionStorage` to the tab, and persists local storage under an explicit `--storage-dir`. Native MCP exposes the upstream state format.
- Temporary profiles are removed on normal completion/cancellation. An abruptly killed host may leave a profile; Windows job-object crash cleanup is not implemented.
- Native commands preserve explicit upstream flags/environment configuration, including network opt-ins. The managed runtime defaults to loopback, one worker, 16 connections, and private-network access disabled.
- Mana's approval UI, embedded preview, click annotation, and takeover are separate integration work; they are not supplied by headless Obscura parity.
