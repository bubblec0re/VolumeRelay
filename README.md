# VolSync

A small Windows tray app that makes the **keyboard volume knob** and the **Windows volume UI** control the physical output device you are actually listening through — even when SteelSeries Sonar/GG has pinned a *virtual* device as the Windows default.

- **Platform:** Windows 10/11 (x64)
- **Stack:** C# / .NET 8 (WinForms) + NAudio.Wasapi 2.2.1
- **Footprint:** single tray icon, no drivers, no services (≈1% of one core at idle, from the 3 s GG poll)

---

## The problem

SteelSeries Sonar/GG sets **"SteelSeries Sonar - Gaming"** (a virtual audio device) as the Windows default output. The volume knob and the Windows flyout only change *that* device's volume. Your physical output — headphones, monitor, speakers — keeps its own separate volume, so the knob does nothing audible.

The audible level is `virtual × physical`. Naively mirroring the knob into both would apply the value **twice** (80% × 80% = 64%).

## How VolSync solves it

**Redirect, not mirror.**

1. The virtual default device is treated as a **neutral pipe**, pinned just below max at **98%**.
2. VolSync listens for volume/mute notifications on that pipe.
3. On a change it computes the **delta** (the pipe moves ~2% per knob detent) and applies that delta to **one selected physical target**.
4. It then snaps the pipe back to 98%.

So the physical device holds the real 0–100% range, the pipe stays audibly transparent (−0.18 dB), and nothing is attenuated twice.

### Why 98% and not 100%

The knob is a **relative** control: ~2% per detent, and **turning up while already at 100% emits no event at all** (verified). Pinning the pipe at exactly 100% would kill the "up" direction. Pinning at 98% leaves one step of headroom, so both directions always produce a full-step event:

| You turn | Pipe goes | Delta applied | Pipe then snaps to |
|---|---|---|---|
| down | 98% → 96% | −2% | 98% |
| up | 98% → 100% | +2% | 98% |

The target still reaches its own true 0% and 100%; only the *target's* ends clamp, which is correct.

---

## Usage

The app lives entirely in the tray. **Right-click** the icon for:

| Item | Meaning |
|---|---|
| **Auto** | Follow whatever device SteelSeries GG is currently routing all render channels to |
| **device names** | Pin one specific output device |
| **Pause sync / Resume sync** | Go fully passive — the pipe behaves natively, nothing is pinned or redirected |
| **Settings…** | Start with Windows |
| **Exit** | Quit |

Hovering the icon shows the current target and its volume, or why there is no target.

### Target selection rules

- Exactly **one** target is controlled at a time. Picking a device *is* the opt-out; there are no per-device checkboxes.
- **Auto** is strict: it only picks a device when *all* configured render channels (`game`, `media`, `chat`, `aux`) route to a **single** device. If routing is split across devices, Auto reports *"split routing — pick a device"* and writes to nothing — one knob must not drive two devices.
- **No volume carryover** — switching targets never copies one device's level onto another. Devices legitimately sit at different levels (a headset at 100%, a monitor at 40%, IEMs at 10%). Copying would be wrong and potentially dangerously loud.
- If the Windows default device *is* the selected target, VolSync goes **passive** and the knob controls it natively.

### Mute

Runtime mute sync is always on: muting the pipe mutes the target, unmuting un-mutes it. The **native Windows sound icon** reflects the pipe, so it correctly shows the muted state.

Mute states are **ignored at startup** on purpose — no syncing, no comparing, no popups. Manage them manually.

---

## Known limitations

- **The virtual device's flyout is a relative jog, not an absolute slider.** Dragging it shifts the target by the delta, then it snaps back to 98%. Mirroring the target's absolute volume into the pipe would reintroduce double attenuation, so this is a deliberate tradeoff. Low impact — Sonar's virtual device volume is rarely touched by hand. **The honest absolute controls are the target device's own flyout/volume mixer and the keyboard knob** — VolSync never touches the target's own UI.
- **Startup is gentle:** the pipe is set to 98%, but the target's existing volume and both mute states are left alone until your first knob turn.
- **No subscription to the target.** If something else changes the target's volume, the tray tooltip can go stale until the next change.
- **GG integration is best-effort.** It depends on an *undocumented* SteelSeries local API (see below). If GG updates and changes it, Auto degrades: it holds the last known target, or falls back to the single physical non-default device, and says so in the tooltip. Manual device selection always works without GG.

---

## Dependencies and credits

VolSync's integration with SteelSeries GG/Sonar was built from community reverse-engineering knowledge (no code copied):

- [wex/sonar-rev](https://github.com/wex/sonar-rev) — MIT
- [PrzemekkkYT/GGSonarRev](https://github.com/PrzemekkkYT/GGSonarRev)

Audio access uses [NAudio](https://github.com/naudio/NAudio) (`NAudio.Wasapi` 2.2.1).

### Disclaimer

The SteelSeries GG/Sonar local HTTP API is **undocumented**. VolSync discovers it via `C:\ProgramData\SteelSeries\GG\coreProps.json` → `https://<ggEncryptedAddress>/subApps` → the Sonar sub-app's `webServerAddress` → `/classicRedirections`. The port is randomized per GG launch, the TLS certificate is self-signed and regenerated per run, and endpoints or JSON shapes may change in **any** GG update. VolSync treats all of it as a disposable layer: every stage is wrapped, and manual target selection remains the foundation.

This project is not affiliated with or endorsed by SteelSeries.

---

## Building

The SDK used during development was installed locally (no system-wide changes):

```powershell
# one-time local SDK install (8.0.427) into .dotnet\
# then:
$env:DOTNET_ROOT = "$PWD\.dotnet"
.\dotnet\dotnet.exe build .\VolSync\VolSync.csproj
```

### Publish a single self-contained exe

```powershell
$env:DOTNET_ROOT = "$PWD\.dotnet"
.\dotnet\dotnet.exe publish .\VolSync\VolSync.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\dist
```

Run `dist\VolSync.exe` directly (not via `dotnet VolSync.exe`).

---

## Manual test checklist

- [ ] Tray icon appears; hovering shows `VolSync — <vol>% → Auto: <device>`
- [ ] Knob **down** lowers the target's volume; the pipe's flyout snaps back to 98%
- [ ] Knob **up** raises it; turning up at 100% does nothing (silent clamp)
- [ ] Repeated detents keep working in both directions (no dead direction)
- [ ] Mute the pipe → the target mutes and the Windows sound icon shows muted; unmute reverses it
- [ ] Selecting a physical device in the tray routes the knob to it; the previous device's volume is untouched
- [ ] Switching back — each device kept its own level (no carryover)
- [ ] **Pause sync** → knob and flyout behave natively, no pinning; **Resume** → pipe snaps back to 98%
- [ ] Auto follows GG when all channels share one device
- [ ] Split GG routing → tooltip says *"split routing — pick a device"*, nothing is written
- [ ] Making the target device the Windows default → VolSync goes passive
- [ ] Changing the Windows default device → the app re-anchors and pins the new pipe
- [ ] Killing SteelSeries GG → Auto degrades gracefully, manual selection still works
- [ ] **Settings → Start with Windows** survives a reboot; unchecking removes `HKCU\...\Run\VolSync`
- [ ] Idle CPU stays low (≈1% of one core — the 3 s GG poll)
