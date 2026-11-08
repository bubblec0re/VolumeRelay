# VolumeRelay

A small Windows tray app that makes the **physical volume knob on your keyboard** and the **Windows volume UI** control the physical output device you are actually listening through — even when SteelSeries Sonar/GG has pinned a virtual device as the Windows default.

- **Platform:** Windows 10/11 (x64)
- **Download:** [latest release](https://github.com/bubblec0re/VolumeRelay/releases/latest) — one self-contained `VolumeRelay.exe`
- **Runtime:** building from source requires the .NET 8 SDK
- **Footprint:** single tray icon, no drivers, no services, no installer

---

## What it does
Allows to use hotkeys and volume knobs to adjust the actual volume of the device that GG routes its outputs into. When SteelSeries Sonar is active, it sets a virtual device ("SteelSeries Sonar - Gaming") as the Windows default output. Physical volume wheels and hotkeys, as well as the Windows volume control then only change *that* virtual device's volume, while your headphones or speakers keep their own separate level — so the knob only changes the volume of whatever apps were routed by Sonar into that virtual device.

VolumeRelay fixes this by:

- keeping the virtual default at a fixed pass-through level (**98%**) and
- applying every volume or mute change to **one physical output device** of your choice.

The result: the knob and the Windows UI control the device you actually hear, and the virtual device stays effectively transparent.

---

## Controls it responds to

| Control | Applied as |
|---|---|
| Keyboard knob / roller / wheel, media Volume Up/Down keys | relative step (~2%) |
| Windows volume UI slider, Settings > Sound, the device slider in Volume Mixer | absolute level |
| Any mute key or mute toggle | mirrored to the target |

Only the Windows **default output device** is watched, so any control that changes its volume is picked up; per-app mixer sliders are not — they change a single app's level, not the device's.

> **Ignore the Windows volume slider.** The native slider, flyout and sound dialog operate on the pinned virtual device, so their displayed values are meaningless — they snap back to ~98% right after every change. The amount you change is still relayed to your target, but for reading and setting the real level, use the VolumeRelay flyout (left-click the tray icon) or the tray tooltip.

---

## Getting started

1. Download `VolumeRelay.exe` from [Releases](https://github.com/bubblec0re/VolumeRelay/releases/latest), or build it yourself (see [AGENTS.md#building](AGENTS.md#building)).
2. Run it. The app starts silently in the notification area — there is no window.
3. Right-click the tray icon and pick your physical output device, or leave **Auto** selected to follow SteelSeries GG.
4. Turn the volume knob. The physical device's level changes — check the real level in the VolumeRelay flyout (left-click the tray icon), not in the Windows volume UI (see [Controls](#controls-it-responds-to)).

To keep it running after a reboot, enable **Start with Windows** in the tray menu.

VolumeRelay stores no settings file and keeps no state of its own. It always starts on **Auto**, and a manual device choice lasts until you exit. The only persisted item is the optional **Start with Windows** registry entry.

---

## Tray menu

| Item | Meaning |
|---|---|
| **Auto** | Follow whatever device SteelSeries GG is currently routing all render channels to |
| **Device names** | Pin one specific physical output device |
| **Pause relay / Resume relay** | Go fully passive — the virtual device behaves natively, nothing is pinned or redirected |
| **Copy diagnostics** | Copies a technical report (device list, engine state, GG API trace, recent events) to the clipboard for pasting into a bug report. Nothing is written to disk; review before sharing. |
| **Start with Windows** | Toggle auto-start; the checkmark reflects the actual registry entry |
| **Exit** | Quit |

### Volume flyout

**Left-click the tray icon** — or just turn the volume knob while VolumeRelay is relaying — to open the VolumeRelay flyout: it shows and controls the **target device's** volume and mute directly, with its own look, deliberately not a Windows-flyout imitation.

- Knob turns pop the flyout with the real target level and keep it live while it is open.
- Drag the slider or click the speaker icon to set the target device's volume or mute directly — the virtual device stays pinned at 98%.
- It never steals focus (safe over fullscreen apps) and auto-hides after ~2.5 s unless hovered; click the tray icon again to dismiss it.
- The native Windows volume flyout is deliberately left untouched: it still appears as usual and still controls the virtual device — exactly the same as without VolumeRelay. When the relay is paused or passive, the flyout says so instead of implying the relay is active.

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
- If the Windows default is a **physical** device — including the selected target — VolumeRelay goes passive and lets the knob control it natively.

### Mute

Mute always follows the virtual device: muting it mutes the target, unmuting un-mutes it. The native Windows sound icon reflects the mute state.

---

## Known limitations

- **The native volume UI still shows the virtual device.** Windows' own flyout, sound dialog and sliders control the virtual device (unchanged behavior); what you drag there is what the target gets, but their display snaps back to 98% afterwards. Read and set the real level in the VolumeRelay flyout (left-click the tray icon) or the tray tooltip.
- **Startup leaves your levels alone.** The virtual device is set to 98%, but the target's volume and both mute states stay untouched until your first knob turn or volume change.
- **The target is not monitored.** If another app changes the target's volume, the tooltip can lag until your next interaction.
- **Only the Windows default output is watched.** Volume controls handled entirely inside a vendor app, without changing the Windows default volume, are not intercepted.
- **SteelSeries GG integration is best-effort.** It relies on an undocumented local API. If a GG update breaks it, Auto falls back to the single physical non-default device and reports the problem in the tooltip. Manual device selection always works without GG.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Windows SmartScreen warns about the downloaded exe | Expected — the build is unsigned and its hash changes with every release. Check the SHA-256 in the [release notes](https://github.com/bubblec0re/VolumeRelay/releases), then choose **More info → Run anyway**. |
| Knob does nothing and the tooltip says `disabled` | The Windows default output is already a real device. Enable Sonar routing, or select your device manually in the menu. |
| Tooltip says `GG unavailable` | SteelSeries GG is not running, or its API changed. Start GG, or pick a device manually. |
| Tooltip says `split routing — pick a device` | Sonar routes different channels to different outputs. Pick a device manually, or set all Sonar channels to one output. |
| The volume slider jumps back to 98% | Expected — the virtual device is kept near max. Read the real level in the VolumeRelay flyout (left-click the tray icon) or the tray tooltip. |
| No devices are listed | No active physical playback devices were found. Connect or enable an output device. |

---

## Releases

GitHub Actions builds and publishes the exe automatically. The stable download link is <https://github.com/bubblec0re/VolumeRelay/releases/latest/download/VolumeRelay.exe>.

---

## Credits

SteelSeries GG/Sonar integration was built from community reverse-engineering knowledge (no code copied):

- [wex/sonar-rev](https://github.com/wex/sonar-rev) — MIT
- [PrzemekkkYT/GGSonarRev](https://github.com/PrzemekkkYT/GGSonarRev)

Audio access uses [NAudio](https://github.com/naudio/NAudio) (`NAudio.Wasapi` 2.2.1).

### Disclaimer

This project is not affiliated with or endorsed by SteelSeries.

VolumeRelay has only been tested on a single machine. If it does not work for you, please open an issue in the [repository](https://github.com/bubblec0re/VolumeRelay/issues).

The code in this repository was written with AI assistance. It is reviewed and maintained by a human developer, but not a C#/Windows audio specialist — read it and use it with that in mind.

---

## Licensing

VolumeRelay is licensed under the [MIT License](LICENSE). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the licenses of bundled dependencies (NAudio).
