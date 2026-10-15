# Arcana

Arcana is Mana's lightweight, agent-focused browser runtime.

It is a standalone sibling project to [Folio](https://github.com/Yuuzulight/Folio). Folio renders Mana's self-contained artifacts; Arcana handles pages that need navigation, JavaScript, a browser session, and agent interaction.

## Current status

The first milestone is the runtime shell:

- one Arcana-owned lifecycle API around the Obscura-derived engine;
- loopback-only CDP endpoints by default;
- Mana-owned temporary profiles and bounded renderer/process settings;
- explicit private-network opt-in;
- health probing and deterministic process-tree cleanup;
- a small CLI for diagnostics and local runtime smoke tests.

Arcana uses the Obscura-derived engine as its primary browser. Edge/Chrome are used only by the external default-browser fallback path when Arcana cannot handle a page. Arcana does not silently start or manage a second browser backend.

## Build and test

Arcana uses the .NET 10 SDK:

```powershell
dotnet build Arcana.slnx
dotnet test Arcana.slnx
```

Inspect the host for usable browser binaries:

```powershell
dotnet run --project src/Arcana.Cli -- doctor
```

Run Arcana with a Mana-owned profile. The process is cleaned up when the command exits:

```powershell
dotnet run --project src/Arcana.Cli -- run --executable=C:\path\to\arcana.exe
```

If an explicit fallback is needed, Arcana asks Windows to open the URL using the registered default browser:

```powershell
dotnet run --project src/Arcana.Cli -- fallback https://example.com
```

## Planned milestones

1. Runtime shell and cleanup around the Arcana engine.
2. Obscura-derived engine adapter and CDP compatibility probes.
3. Agent page model: navigation, DOM snapshot, links, forms, screenshots, and controlled input.
4. Mana policy layer: approvals, network rules, profile ownership, redaction, and takeover.
5. In-Mana preview surface and click-to-annotate support.
6. Obscura parity suite and measured RAM/CPU startup and page-load budgets.

## License

Apache-2.0, same as Mana and Folio.
