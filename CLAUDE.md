# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Przepustnica** ("throttle/valve" in Polish) is a Windows desktop application for per-application network bandwidth limiting. Implementation is underway following `PLAN_WDROZENIA_WINDOWS.md`; **Etapy 0–3 are done** (UI shell, real monitoring service + IPC with pipe auth token, WinDivert limit enforcement incl. schedule blocking and multiple concurrent rules, "saved" KPI, service registration + MSI installer). Remaining: code signing, process churn edge cases. What exists:

- `ui/` — Tauri 2 + React 19 (TypeScript, Vite) production UI implementing the four prototype screens. `ui/src-tauri/src/lib.rs` holds the named-pipe bridge to the service (auto-reconnect; re-emits service JSON lines as `backend-message` events, connectivity as `backend-status`). Without a service connection the UI runs a local demo simulator.
- `service/PrzepustnicaService/` — C#/.NET 8 backend (console mode for dev, Windows Service-ready via `Microsoft.Extensions.Hosting.WindowsServices`): per-PID traffic counter (kernel ETW), rule matching by exe name, SQLite persistence (`%ProgramData%\Przepustnica\przepustnica.db`), named-pipe IPC server, and the `Throttler` — WinDivert Network-layer capture with a GCRA pacer per rule (delays, never drops, packets over the limit) plus packet dropping for rules outside their schedule window ("Uśpiona").
- `throttle-poc/` — Original WinDivert token-bucket proof of concept (C#), now superseded by `service/.../Throttler.cs`. Kept as a minimal reference; run elevated: `dotnet run -- <processName> <rateMbps>`.
- `service/install-service.ps1` / `uninstall-service.ps1` — register/unregister the published service as a Windows Service (LocalSystem, autostart, failure restart). Keep these scripts **ASCII-only**: Windows PowerShell 5.1 reads BOM-less `.ps1` as ANSI and Polish diacritics break parsing.
- `installer/` — WiX 5 MSI authoring (`Przepustnica.wxs`, stable UpgradeCode) + `build-installer.ps1` (tauri build → dotnet publish → wix build). WiX 7 requires an OSMF license acceptance — stay on WiX 5 (`dotnet tool install --global wix --version 5.0.2`).
- `Przepustnica.dc.html` — High-fidelity HTML/CSS/React prototype of all four UI screens, viewable in a browser
- `support.js` — The dc-runtime: a compiled browser-side framework that powers `.dc.html` design documents (not to be edited; rebuilt externally via `cd dc-runtime && bun run build`)
- `README.md` — Main project documentation (Polish): architecture, install/build instructions, user guide, IPC reference, troubleshooting
- `docs/DESIGN_HANDOFF.md` — Screen-by-screen UI specification with design tokens and interaction details
- `PLAN_WDROZENIA_WINDOWS.md` — Full technical implementation plan for the Windows production app

## Production Architecture

Two-process model separated by privilege boundary:

```
UI process (Tauri 2, no admin)
  └─ IPC (named pipe + auth token)
       └─ Windows Service (SYSTEM / admin)
              ├─ Rule engine (limit + schedule)
              ├─ Throttler (token-bucket per PID, WinDivert 2.x)
              ├─ Traffic counter (ETW: Microsoft-Windows-Kernel-Network)
              └─ SQLite (rules + history)
```

**IPC contract** (implemented; newline-delimited camelCase JSON on `\\.\pipe\przepustnica`):
- UI → Service commands: `setRule`, `deleteRule`, `toggle`, `getRules`, `listProcesses`, `getHistory`
- Service → UI: `rules` (pushed on connect and after every change), `usage` telemetry at 1 Hz (`{apps: {id: {mbps, downMbps, upMbps, throttled, status}}, totalMbps, todayGb}`), `processes`, `history`, `error`
- Service-side gotchas already fixed once — don't regress them: pipe buffers must be non-zero (0-byte buffers deadlock a client that writes before reading) and per-client outgoing messages go through a bounded queue so a hung client can't stall the broadcast. PID→exe mapping comes from ETW process rundown events (a polled process list misses short-lived processes).
- Throttler-side gotchas: connection→PID must be event-driven via a second WinDivert handle at the **Socket layer** (sniff+recv-only) — the polled `GetExtendedTcpTable` misses connections that live under one refresh interval, letting fast downloads escape the limit entirely. Likewise PID→exe must be resolved **at socket-event time** (`Process.GetProcessById` while the process provably lives); the ETW rundown lags ~1 s, longer than a fast transfer. Delayed packets are re-injected off the capture loop; non-matching traffic is re-injected inline.

**Planned stack:**

| Layer | Choice |
|---|---|
| Desktop shell | Tauri 2 (Rust) |
| Frontend | Existing HTML/CSS/JS or React |
| Backend service | C#/.NET 8 Windows Service |
| Packet throttling | WinDivert 2.x |
| Traffic monitoring | ETW |
| Database | SQLite |
| Installer | WiX (MSI) + Authenticode |

## UI Screens (Prototype)

Four screens are fully designed in the prototype:
- **2a** — App list with KPI cards and per-app status table
- **2b** — Add application modal (speed slider + schedule picker)
- **2c** — Live monitor with real-time usage chart
- **2d** — History with bar charts and transfer statistics

## Prototype Tech Stack

The `.dc.html` prototype runs entirely in-browser:
- React 18.3.1 + ReactDOM (CDN/UMD)
- Babel standalone 7.29.0 (JSX transpilation)
- dc-runtime (`support.js`) — custom component framework
- Fonts: **Space Grotesk** (headings/numbers), **Manrope** (UI text)

## App State Shape (Designed)

```js
{
  apps: [{ id, name, exeMatch, iconColor, initials, limitMbps, schedule: { mode, from, to }, enabled }],
  liveUsage: { [appId]: { mbps, throttled: bool } },
  history: { range, series: { [appId]: number[] } },
  ui: { activeNav, addModalOpen, monitorRange, historyPeriod }
}
```

## Build Commands

- **Service** (`service/PrzepustnicaService/`): `dotnet build`; `dotnet run` for dev console mode. Must run **elevated** or the ETW kernel session fails (the service degrades to zero counters but keeps serving IPC). Data lives in `%ProgramData%\Przepustnica\przepustnica.db`.
- **UI** (`ui/`): `npm run tauri dev` (needs `cargo` on PATH — in Git Bash it's at `~/.cargo/bin` but not on PATH by default); frontend-only typecheck+build: `npm run build`.
- **Throttling PoC** (`throttle-poc/`): `dotnet run -- <processName> <rateMbps>`, elevated.
- **Installer** (`installer/`): `powershell -File build-installer.ps1` → `Przepustnica-0.1.0.msi` (UI + service, service auto-registers with autostart). Service publish is **self-contained win-x64** (no .NET runtime needed on target); the wxs harvest must stay recursive — `dist/service/amd64/KernelTraceControl.dll` is required for ETW.
- **Service as Windows Service**: `dotnet publish -c Release -r win-x64 --self-contained true -o dist/service`, then `service/install-service.ps1` (elevated).
- Prototype: open `Przepustnica.dc.html` directly in a browser.

Dev workflow: start the service elevated, then `npm run tauri dev` — the sidebar shows "Usługa połączona" when the pipe connects (auto-reconnects every 2 s). Without the service the UI runs its demo simulator.
