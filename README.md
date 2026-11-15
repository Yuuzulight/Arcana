# Arcana

Arcana is Mana's lightweight, agent-focused browser runtime. Arcana owns browser sessions and the [Page API](docs/page-api.md). Its default backend on this branch is the sandboxed [Folio](https://github.com/Yuuzulight/Folio) content host.

Arcana 1.0 ships with Folio. The source tree still has the historical [Obscura](https://github.com/h4ckf0r0day/obscura) baseline for migration and parity testing. See the [engine patches](docs/engine-patches.md) and [NOTICE](NOTICE).

The engine-neutral Page API contract covers pages, stable references, forms, input, capture, storage, tabs, and event subscriptions. See the [roadmap](docs/roadmap.md) and [parity matrix](docs/parity.md) for backend coverage and qualification status.

The Obscura source pin lives in `engine.lock.json`. The patches in `engine-patches/` cover CDP child frames, multipart uploads, and frame navigation. Arcana's explicit fallback opens the browser registered as the Windows default.

## Install, build, and test

### Windows release package

The combined Folio qualification workflow builds a self-contained Windows x64
ZIP from the Arcana revision under test and the full Folio host publish. Download
`qualified-windows-package`, verify the ZIP against its `.sha256` file, extract
the archive, and run `Arcana.Cli.exe doctor`. The archive includes the .NET
runtime. `arcana-build.json` records the Arcana and Folio revisions and broker
hash. The qualification workflow also opens an HTTPS fixture using the
extracted package.

To package locally from a clean checkout and a clean Folio native publish:

```powershell
./tools/package-win-x64.ps1 -FolioPublish $folioPublish -FolioCommit $folioCommit -OutputZip ./artifacts/arcana-win-x64.zip
```

### Source build for the historical parity baseline

Windows x64 and .NET 10 SDK:

```powershell
./tools/fetch-engine.ps1
# Builds the pinned rendering-and-stealth Obscura revision from source, applying Arcana's engine patches.
dotnet build Arcana.slnx
$env:ARCANA_TEST_ENGINE = (Resolve-Path engines/obscura/current/obscura.exe).Path
dotnet test Arcana.slnx
```

The engine and its worker stay together. Discovery checks `--executable=PATH`, `ARCANA_ENGINE`, local engine directories, and PATH. Use `-Variant render` with the installer for the ordinary rendering build. The source installer requires Git, Rust/Cargo, and the Windows C++ build tools. `-UseRelease` (the last tagged upstream archive) is refused while patches are listed in `engine.lock.json`, because that archive has none of them. Engine tests are explicitly skipped when `ARCANA_TEST_ENGINE` is absent; policy and CDP transport tests still run. CI installs the engine and executes the full suite.

## Browser commands

All native options are available after `engine`. The four native commands also work directly:

```powershell
dotnet run --project src/Arcana.Cli -- fetch https://example.com --dump markdown
dotnet run --project src/Arcana.Cli -- fetch https://example.com --screenshot page.png
dotnet run --project src/Arcana.Cli -- scrape https://example.com https://example.org --eval document.title --concurrency 2
dotnet run --project src/Arcana.Cli -- serve --port 9222
dotnet run --project src/Arcana.Cli -- engine --stealth --proxy http://localhost:8080 fetch https://example.com --dump text
dotnet run --project src/Arcana.Cli -- engine --help
```

Native stdin/stdout/stderr are relayed as bytes, preserving binary fetch output and MCP JSON lines. Native exit codes propagate. Temporary profiles are cleaned up on completion or Ctrl+C; supply the engine's `--storage-dir` for deliberate persistence. Native flags and environment settings retain upstream semantics.

## MCP

```powershell
dotnet run --project src/Arcana.Cli -- mcp
dotnet run --project src/Arcana.Cli -- mcp --http --port 3000
```

For a client configuration, build first, then set the command to `dotnet` with arguments:

```json
["C:/ManaAI/Arcana/src/Arcana.Cli/bin/Debug/net10.0/Arcana.Cli.dll", "mcp"]
```

Adjust the DLL path for your checkout or published build. All upstream browser tools and their schemas are retained, including form interaction, extraction, search, navigation, cookies/storage, tabs, screenshots, and PDF. The underlying MCP server identifies itself as Obscura. HTTP MCP uses upstream bearer authentication and origin controls; see the [upstream guide](https://github.com/h4ckf0r0day/obscura/blob/bd9529418b7fa975b50eabeac415cfaaa865a588/docs/Use-the-MCP-server.md).

## .NET runtime

```csharp
using Arcana.Runtime;

await using var runtime = new BrowserRuntime(new ArcanaOptions());
await runtime.StartAsync();
await using var browser = await ArcanaBrowser.ConnectAsync(runtime);
var page = await browser.NewPageAsync("https://example.com");
var snapshot = await page.SnapshotAsync();
Console.WriteLine(snapshot.Text);
Console.WriteLine(await page.GetMarkdownAsync());
await File.WriteAllBytesAsync("page.png", await page.CaptureScreenshotAsync(fullPage: true));
await File.WriteAllBytesAsync("page.pdf", await page.PrintPdfAsync());
```

The versioned loopback page transport is available from the CLI. It prints one JSON ready record containing the endpoint and bearer token:

```powershell
dotnet run --project src/Arcana.Cli -- api --port=0
```

Page methods also provide fill/type/select/check, batch forms, waits, history, cookies, storage export/restore, viewport, coordinate input, and scrolling. Browser methods list/attach/close tabs and manage isolated contexts. `SendCdpAsync` exposes additional native methods. Subscribe **before** an operation to retain its events:

```csharp
using var console = page.Subscribe("Runtime.consoleAPICalled");
await page.EvaluateAsync("console.log('hello')");
var message = await console.ReadAsync();
```

Use `Subscribe("Fetch.requestPaused")` and `SendCdpAsync("Fetch.enable", ...)` for live interception, or `Page.screencastFrame` for a preview consumer. CDP responses are correlated independently of events, so a pending evaluation can receive interception replies. Event streams are bounded and report overflow explicitly.

The managed runtime uses loopback, one worker, 16 connections, an owned temporary profile, and private-network access disabled. `AllowPrivateNetwork`, `Proxy`, `UserAgent`, `Stealth`, `WorkerCount`, and `MaxConnections` are explicit configuration settings. Input values are omitted from the .NET snapshot. Storage exports intentionally contain session secrets.

## Arcana-specific commands

```powershell
dotnet run --project src/Arcana.Cli -- doctor
dotnet run --project src/Arcana.Cli -- snapshot https://example.com
dotnet run --project src/Arcana.Cli -- run
dotnet run --project src/Arcana.Cli -- fallback https://example.com
```

Snapshot references belong to the current .NET page/document. The helper covers the top document, up to 500 controls and 12,000 text characters, and uses DOM/CSS visibility. DOM click/fill and CDP coordinate input are separate APIs. Read the [architecture](docs/architecture.md) and [parity limits](docs/parity.md) before integrating a visual agent.

## Next integration work

Mana approvals, embedded preview, click annotations, visible takeover, crash-resilient Windows job cleanup, and measured performance budgets remain Arcana-specific work. They are separate from access to Obscura's headless engine capabilities.

## License

Apache-2.0. See [NOTICE](NOTICE) for engine provenance.
