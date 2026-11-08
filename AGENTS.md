# AGENTS.md

Guide for AI agents (and new humans) working in this repository.

## What this is

**VolumeRelay** — a Windows 10/11 tray-only app (no window, no installer, no config file) that makes the physical keyboard volume knob and the Windows volume UI control the *physical* output device you're listening through, even when SteelSeries Sonar/GG pins a virtual device as the Windows default.

Mechanism: the virtual "pipe" device is pinned at ~98% pass-through; every volume/mute **delta** observed on the Windows default endpoint is applied to one chosen physical target, then the pipe is reset.

- Target: `net8.0-windows`, WinForms (`UseWindowsForms`), single dependency: `NAudio.Wasapi` 2.2.1
- Single instance via named mutex (`VolumeRelay_SingleInstance`); crashes are dumped to `%TEMP%\volumerelay-crash.log`
- No settings file, no state of its own: always starts on **Auto**; the only persisted item is the optional `Start with Windows` HKCU Run entry

## Repository layout

```
VolumeRelay/            # the app (all C# lives here)
  VolumeRelay.csproj
  Program.cs            # entry point: mutex, Application.Run(new TrayApp())
  TrayApp.cs            # hidden form + NotifyIcon; tray menu & tooltip (UI thread)
  VolumeRelayEngine.cs  # core: delta-based WASAPI redirect, device state, RelayMode
  GgClient.cs           # SteelSeries Sonar local HTTP API client (undocumented, best-effort)
  DeviceWatcher.cs      # IMMNotificationClient COM callback for endpoint events
  DeviceHelper.cs       # enumerates render endpoints; virtual-vs-physical via HardwareIds property
  DeviceInfo.cs         # record: Id, Name, IsVirtual, IsDefault
  AutoStart.cs          # HKCU Run key toggle
  app.manifest
Spike/                  # throwaway experiment project (device enumeration probing). NOT part of the release.
dist/VolumeRelay.exe    # last built/published exe (gitignored, keep out of source changes)
.github/workflows/dotnet-desktop.yml  # CI: publish self-contained exe, auto GitHub Release
```

## Building

Requires the **.NET 8 SDK**. If the global `dotnet` CLI is missing, look for a local SDK install (e.g. a gitignored `.dotnet/` folder in the repo root) before installing anything. Machine-specific build notes, if present, live in `BUILD-LOCAL.md` (gitignored — it won't exist on a fresh clone).

Build:

```powershell
dotnet build VolumeRelay/VolumeRelay.csproj -v q
```

Release exe (mirrors CI):

```powershell
dotnet publish VolumeRelay/VolumeRelay.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
```

There is no test project. Verify changes by running `dist\VolumeRelay.exe` (or `build` output) and watching the **tray icon tooltip** — it is the only window into engine state.

- The single-instance mutex means a second launch just shows a message box and exits — kill the previous instance before testing.
- Relaying is only observable with SteelSeries Sonar/GG running and a virtual device pinned as the Windows default; without that the engine is deliberately passive (see the `RelayMode` states).

## Architecture notes (read before touching the engine)

- **Delta-based, not level-based.** `VolumeRelayEngine` intercepts volume changes on the default (virtual) endpoint, computes the delta, applies it to the target, and snaps the pipe back to `DefaultPin` (0.98). Don't convert this to "copy absolute levels" — the pin is what makes the knob work.
- **Event-context heuristic (measured, load-bearing).** HID knob presses move the pipe in exact 0.02 steps with *no* event context; UIs that set a level *pass* one. `context present` ⇒ absolute set, `absent` ⇒ relative nudge. If knob turns ever jump the target to full volume, this assumption broke. See the comment block above `Epsilon` in `VolumeRelayEngine.cs`.
- **COM/threadpool events.** `StateChanged`/`VolumeChanged` fire off the UI thread; `TrayApp` marshals with `BeginInvoke`. The hidden form's handle is created in the constructor for this reason — don't remove `CreateHandle()`.
- **Never re-enter Core Audio from a notification callback.** Endpoint callbacks arrive on a Core Audio callback thread where re-entering (enumerating, disposing, activating) can block indefinitely. `QueueRefresh()` only marks work dirty; the real refresh runs on a pool thread after a 150 ms settle so plug/unplug bursts coalesce.
- **COM objects must stay rooted.** `_enumerator`/`_watcher` are long-lived engine fields on purpose — as locals they'd be collected after the first GC, silently killing hotplug and default-change events.
- **Self-write suppression.** The engine's own pipe reset to the pin fires its own volume notification; `_lastPipeWrite` + `SuppressWindowMs` + the pin-value check in `OnPipeChanged` break the feedback loop. Keep this intact when changing pin logic.
- **GG polling.** A 3 s `System.Timers.Timer` runs `PollAutoTarget()`; an auto-target change is picked up within ~3 s (plus an immediate poll at startup and on device changes).
- **`DeviceWatcher`'s `[Guid]` is fixed** — it is the COM class ID for endpoint notification registration; don't regenerate it.
- **Auto target resolution:** `QueryGgTarget()` (strict rule: all Sonar render channels `game/media/chat/aux` must map to one device) with fallback `HeuristicTarget()` (single physical non-default device). Split routing or GG down ⇒ `NoTarget` mode, reported in the tooltip.
- **Passive states are not bugs.** If the Windows default is already physical, or *is* the selected target, the engine deliberately does nothing (`RelayMode.DefaultNotVirtual` / `DefaultIsTarget`). The tooltip wording for these is intentional.
- **Virtual detection** uses the endpoint `HardwareIds` property store (`{1}.ROOT\MEDIA\...` = virtual). Only physical devices are ever valid targets.
- **GG API is undocumented** (discovery: `coreProps.json` → `/subApps` → sonar port → `/classicRedirections`). Every stage is try/catch by design; a GG update may break Auto. Manual device selection must always keep working.
- `AutoStart.cs` writes the HKCU Run registry key — only from the in-app toggle, default off.
- `NotifyIcon.Text` throws above 127 chars; tooltip strings are clipped in `TrayApp.Clip`.

## Conventions

- Comments in the engine record *measured* behavior (step sizes, context presence, property shapes). Treat them as specs; don't delete them when refactoring.
- Keep the app footprint: no new windows, no new persisted state, no services, no dependencies without strong reason.
- `README.md` is the user-facing behavioral spec (mute rules, no volume carryover, passive states); keep it in sync when behavior changes, including its tooltip/menu tables.
- Release flow (`.github/workflows/dotnet-desktop.yml`): push to `master` → rolling "Latest build"; push a `v*` tag → versioned release (version = tag minus `v`). Don't hand-edit `dist/`.
