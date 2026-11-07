# VolumeRelay

A small Windows tray app that makes the **keyboard volume knob** and the **Windows volume UI** control the physical output device you are actually listening through — even when SteelSeries Sonar/GG has pinned a *virtual* device as the Windows default.

- **Platform:** Windows 10/11 (x64)
- **Stack:** C# / .NET 8 (WinForms) + NAudio.Wasapi 2.2.1
- **Footprint:** single tray icon, no drivers, no services (≈1% of one core at idle, from the 3 s GG poll)

---

## The problem

SteelSeries Sonar/GG sets **"SteelSeries Sonar - Gaming"** (a virtual audio device) as the Windows default output. The volume knob and the Windows flyout only change *that* device's volume. Your physical output — headphones, monitor, speakers — keeps its own separate volume, so the knob does nothing audible.

The audible level is `virtual × physical`, so putting the knob's value on both devices would attenuate twice (80% × 80% = 64%).

## How VolumeRelay solves it

VolumeRelay keeps the virtual default at a fixed pass-through level and moves the real level onto one physical device:

1. The virtual default device is treated as a **neutral pipe**, pinned just below max at **98%**.
2. VolumeRelay listens for volume/mute notifications on that pipe.
3. On a change it works out what the change *means*: a **knob detent** is a relative ~2% step and is added to the target's level, while the **flyout slider** is absolute and sets the target to the level you dragged to.
4. It then snaps the pipe back to 98%.

So the physical device holds the real 0–100% range, the pipe stays audibly transparent (−0.18 dB), and nothing is attenuated twice.

### Telling the slider from the knob

Both arrive as the same WASAPI notification, carrying only the new volume — but not quite: a UI that sets a level of its own passes an **event-context GUID**, while the HID volume keys (the knob) pass **none**. So the *presence* of a context is what separates "something set this level" from "a hotkey nudged it":

| Notification | Meaning | Applied as |
|---|---|---|
| has an event context | a UI set this level | absolute — target := value |
| no event context | a hotkey detent | relative — target += step |

The GUID *values* are per-caller and undocumented — the taskbar flyout and the Settings app each use their own — so they are never compared, only tested for presence. That is also why a volume UI nobody has written yet works without touching this code.

The rule rests on one measured assumption: **the knob always passes an empty context.** If a knob turn ever jumps the target to full volume, that assumption is what broke.

### Why 98% and not 100%

The knob is a **relative** control: ~2% per detent, and **turning up while already at 100% emits no event at all**. Pinning the pipe at exactly 100% would kill the "up" direction. Pinning at 98% leaves one step of headroom, so both directions always produce a full-step event:

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
| **device names** | Pin one specific output device. Only **physical** outputs are listed — see below |
| **Pause relay / Resume relay** | Go fully passive — the pipe behaves natively, nothing is pinned or redirected |
| **Start with Windows** | Toggle auto-start; the checkmark reflects the actual `HKCU\...\Run` entry |
| **Exit** | Quit |

Hovering the icon reports what the app is currently doing: the target and its volume while it is relaying, `disabled` when your knob is already driving a real device directly, `paused`, or why there is no target at all.

### Which devices are listed

Only **physical** outputs appear in the menu — a virtual device is never a valid target, and the default/virtual pipe is itself virtual.

Virtual devices are recognised from the device's **HardwareIds**, not its name. Windows enumerates software audio devices on the `ROOT` bus, while real hardware always sits on a physical bus:

| Device | HardwareIds |
|---|---|
| SteelSeries Sonar - Gaming (virtual) | `{1}.ROOT\MEDIA\0000` |
| USB headphones | `{1}.USB\VID_xxxx&PID_xxxx&MI_00\…` |
| Onboard / GPU audio | `{1}.HDAUDIO\FUNC_01&VEN_xxxx&…` |

Because this is a property of the device rather than its name, an unfamiliar virtual driver is still hidden and a real device with an odd name is still listed. Name matching is only a fallback for the rare case where the property store cannot be read.

### Target selection rules

- Exactly **one** target is controlled at a time; picking a different device switches the target.
- **Auto** is strict: it only picks a device when *all* configured render channels (`game`, `media`, `chat`, `aux`) route to a **single** device. If routing is split across devices, Auto reports *"split routing — pick a device"* and writes to nothing — one knob must not drive two devices.
- **Switching targets never copies volume.** Each device keeps its own level — a headset at 100%, a monitor at 40%, IEMs at 10% — because those differences are intentional, and copying one onto another can be dangerously loud.
- If the Windows default device *is* the selected target, VolumeRelay goes **passive** and the knob controls it natively.
- VolumeRelay only acts when the Windows default is a **virtual** device. If the default is physical, the knob already controls something real: redirecting would attenuate twice and pinning it to 98% would overwrite a level you chose on purpose. This is also what keeps a slow-GG boot safe — it waits for Sonar to become the default rather than pinning whatever was there first.
- **No config file.** VolumeRelay writes nothing to disk and keeps no state of its own. It always starts on **Auto** — following GG is the entire point — and a manual pick lasts until you exit. The only thing it ever persists is the optional `HKCU\...\Run` entry behind *Start with Windows*, and that lives in the registry, not a file.

### Mute

Mute always follows the pipe: muting the pipe mutes the target, unmuting un-mutes it. The **native Windows sound icon** reflects the pipe, so it correctly shows the muted state.

Mute states are **ignored at startup** on purpose: VolumeRelay reads neither device's mute state and takes no action until you change one.

---

## Known limitations

- **The virtual device's flyout cannot *display* the target's level.** What you drag to is what the target gets, but the slider returns to 98% afterwards, because the pipe has to stay near max to keep the knob's headroom. The honest *displays* are the target device's own flyout/volume mixer and the tray tooltip. (VolumeRelay never touches the target's own UI.)
- **Startup leaves your levels alone:** the pipe is set to 98%, but the target's current volume and both mute states are untouched until your first knob turn.
- **The target is not monitored.** If something else changes the target's volume, the tooltip can lag until your next interaction.
- **GG integration is best-effort.** It depends on an *undocumented* SteelSeries local API (see below). If GG updates and changes it, Auto degrades: it holds the last known target, or falls back to the single physical non-default device, and says so in the tooltip. Manual device selection always works without GG.

---

## Dependencies and credits

VolumeRelay's integration with SteelSeries GG/Sonar was built from community reverse-engineering knowledge (no code copied):

- [wex/sonar-rev](https://github.com/wex/sonar-rev) — MIT
- [PrzemekkkYT/GGSonarRev](https://github.com/PrzemekkkYT/GGSonarRev)

Audio access uses [NAudio](https://github.com/naudio/NAudio) (`NAudio.Wasapi` 2.2.1).

### Disclaimer

The SteelSeries GG/Sonar local HTTP API is **undocumented**. VolumeRelay discovers it via `C:\ProgramData\SteelSeries\GG\coreProps.json` → `https://<ggEncryptedAddress>/subApps` → the Sonar sub-app's `webServerAddress` → `/classicRedirections`. The port is randomized per GG launch, the TLS certificate is self-signed and regenerated per run, and endpoints or JSON shapes may change in **any** GG update. VolumeRelay treats all of it as best-effort: every stage is wrapped, so a broken API costs you Auto, not the app.

This project is not affiliated with or endorsed by SteelSeries.

---

## Building

Any .NET 8 SDK will do:

```powershell
dotnet build .\VolumeRelay\VolumeRelay.csproj
```

### Publish a single self-contained exe

```powershell
dotnet publish .\VolumeRelay\VolumeRelay.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\dist
```

Run `dist\VolumeRelay.exe` directly (not via `dotnet VolumeRelay.exe`).

During development the SDK was installed locally into `.dotnet\` (gitignored) to avoid a system-wide install. If you have one there, set `$env:DOTNET_ROOT = "$PWD\.dotnet"` and call `.\dotnet\dotnet.exe` instead.

