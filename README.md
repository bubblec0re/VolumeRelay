# VolumeRelay

A small Windows tray app that makes the **physical volume knob on your keyboard** and the **Windows volume UI** control the physical output device you are actually listening through — even when SteelSeries Sonar/GG has pinned a virtual device as the Windows default.

- **Platform:** Windows 10/11 (x64)
- **Download:** [latest release](https://github.com/bubblec0re/VolumeRelay/releases/latest) — one self-contained `VolumeRelay.exe`
- **Runtime:** building from source requires the .NET 8 SDK
- **Footprint:** single tray icon, no drivers, no services, no installer

---

## What it does

When SteelSeries Sonar is active, it sets a virtual device ("SteelSeries Sonar - Gaming") as the Windows default output. Physical volume wheels and hotkeys, as well as the Windows volume control then only change *that* virtual device's volume, while your headphones or speakers keep their own separate level — so the knob only changes the volume of whatever apps were routed by Sonar into that virtual device.

VolumeRelay fixes this by:

- keeping the virtual default at a fixed pass-through level (**98%**) and
- applying every volume or mute change to **one physical output device** of your choice.

The result: the knob and the Windows UI control the device you actually hear, and the virtual device stays effectively transparent.

---

## Controls it responds to

A physical rotary knob, roller, or wheel built into a keyboard, dedicated Volume Up/Down keys, or anything else that emits the standard Windows volume keys. VolumeRelay treats those as **relative steps** (about 2% per detent or key press). Standard keyboard **media keys** (Volume Up / Volume Down / Mute) take the same path and behave identically.

| Control | Applied as |
|---|---|
| Keyboard knob / roller / wheel, media Volume Up/Down keys | relative step (~2%) |
| Windows volume flyout, Settings > Sound, the device slider in Volume Mixer | absolute level |
| Any mute key or mute toggle | mirrored to the target |

Dragging the flyout slider to 40% sets the target to 40%; nudging the knob up once raises it by one step. VolumeRelay watches only the Windows **default output device**, so any control that changes that device's volume is picked up. Per-app sliders in the volume mixer are not — they change a single app's level, not the device's.

---

## Getting started

1. Download `VolumeRelay.exe` from [Releases](https://github.com/bubblec0re/VolumeRelay/releases/latest), or build it yourself (see [Building](#building)).
2. Run it. The app starts silently in the notification area — there is no window.
3. Right-click the tray icon and pick your physical output device, or leave **Auto** selected to follow SteelSeries GG.
4. Turn the knob or move the Windows volume slider. The physical device's level changes.

To keep it running after a reboot, enable **Start with Windows** in the tray menu.

VolumeRelay stores no settings file and keeps no state of its own. It always starts on **Auto**, and a manual device choice lasts until you exit. The only persisted item is the optional **Start with Windows** registry entry.

---

## Tray menu

| Item | Meaning |
|---|---|
| **Auto** | Follow whatever device SteelSeries GG is currently routing all render channels to |
| **Device names** | Pin one specific physical output device |
| **Pause relay / Resume relay** | Go fully passive — the virtual device behaves natively, nothing is pinned or redirected |
| **Start with Windows** | Toggle auto-start; the checkmark reflects the actual registry entry |
| **Exit** | Quit |

### Tooltip

Hovering the tray icon reports the current state:

| Tooltip | Meaning |
|---|---|
| `VolumeRelay — 42%: Headphones` | Relaying; the target's name and current volume |
| `VolumeRelay — paused` | Relay paused |
| `VolumeRelay — disabled: real device … selected` | The Windows default is already a real device, so the knob controls it natively |
| `VolumeRelay — GG unavailable` | Auto cannot reach SteelSeries GG |
| `VolumeRelay — split routing — pick a device` | Sonar's channels route to different devices; pick one manually |

---

## Target selection

- Only **physical** outputs are listed in the menu; virtual devices are never valid targets.
- Exactly **one** target is controlled at a time. Selecting another device switches the target.
- **Auto** picks a device only when all of Sonar's render channels (`game`, `media`, `chat`, `aux`) route to a **single** device. If routing is split, Auto controls nothing and asks you to pick a device.
- **Switching targets never copies volume.** Each device keeps its own level (a headset at 100%, a monitor at 40%, IEMs at 10%).
- If the Windows default device *is* the selected target, VolumeRelay goes passive and lets the knob control it natively.
- VolumeRelay only redirects when the Windows default is a **virtual** device. If the default is physical, the knob already controls something real, so the app stays out of the way.

### Mute

Mute always follows the virtual device: muting it mutes the target, unmuting un-mutes it. The native Windows sound icon reflects the mute state.

---

## Known limitations

- **The virtual device's flyout cannot display the target's level.** What you drag to is what the target gets, but the slider snaps back to 98% afterwards. Use the target device's own flyout/volume mixer or the tray tooltip to read the real level.
- **Startup leaves your levels alone.** The virtual device is set to 98%, but the target's volume and both mute states stay untouched until your first knob turn or volume change.
- **The target is not monitored.** If another app changes the target's volume, the tooltip can lag until your next interaction.
- **Only the Windows default output is watched.** Volume controls handled entirely inside a vendor app, without changing the Windows default volume, are not intercepted.
- **SteelSeries GG integration is best-effort.** It relies on an undocumented local API. If a GG update breaks it, Auto falls back to the single physical non-default device and reports the problem in the tooltip. Manual device selection always works without GG.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Knob does nothing and the tooltip says `disabled` | The Windows default output is already a real device. Enable Sonar routing, or select your device manually in the menu. |
| Tooltip says `GG unavailable` | SteelSeries GG is not running, or its API changed. Start GG, or pick a device manually. |
| Tooltip says `split routing — pick a device` | Sonar routes different channels to different outputs. Pick a device manually, or set all Sonar channels to one output. |
| The flyout slider jumps back to 98% | Expected — the virtual device is kept near max. Read the real level on the target device. |
| No devices are listed | No active physical playback devices were found. Connect or enable an output device. |

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

### Releases

GitHub Actions builds and publishes the exe automatically:

- every push to `master` that touches `VolumeRelay/` updates the rolling **Latest build** release;
- pushing a `v*` tag (for example `v1.1.0`) creates a versioned release;
- either can be triggered by hand from the **Actions** tab.

The stable download link is <https://github.com/bubblec0re/VolumeRelay/releases/latest/download/VolumeRelay.exe>.

---

## Credits

SteelSeries GG/Sonar integration was built from community reverse-engineering knowledge (no code copied):

- [wex/sonar-rev](https://github.com/wex/sonar-rev) — MIT
- [PrzemekkkYT/GGSonarRev](https://github.com/PrzemekkkYT/GGSonarRev)

Audio access uses [NAudio](https://github.com/naudio/NAudio) (`NAudio.Wasapi` 2.2.1).

### Disclaimer

The SteelSeries GG/Sonar local HTTP API is **undocumented**. Endpoints and JSON shapes may change in any GG update. VolumeRelay treats the integration as best-effort: if it breaks, you lose Auto, not the app.

This project is not affiliated with or endorsed by SteelSeries.
