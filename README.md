# Star Button

Windows desktop tool for Star Citizen joysticks. It keeps your sticks, controller profiles and key rules stable,
and writes the Star Citizen bindings for the *current* `jsN` numbers into `actionmaps.xml`.

## What it does

- **Sticks** – lists every DirectInput controller. Name them once; the tool re-finds them after re-plugging or a
  reboot. Identical sticks (no serial number) can be re-linked in one click ("Identify" – press a button).
  Live view of buttons (lit when pressed), axes and hats, plus an event log.
- **Button rules** – per button: *on press* and *on release* send a key or combination (e.g. `U`, `LShift+D`),
  optionally *hold while pressed*. Runs while the app is running (tray).
- **Bindings** – the full action list is read from `Data.p4k` (`defaultProfile.xml` + `global.ini`) and cached per
  game build, so a patch that adds actions shows up after a refresh. Bind by pressing a button on a stick.
  Conflicts, defaults and what is currently in your `actionmaps.xml` are shown. Pressing any physical button
  shows everything that lives on it.
- **Profiles** (Bindings tab) – a profile is a complete setup (keyboard & mouse, gamepad, every stick). Switch,
  create, duplicate, rename, delete and export/import from the header of the Bindings tab. "Read from Star
  Citizen" turns the current `actionmaps.xml` into a profile.
- **Deploy** (Bindings tab) – resolves stick → `jsN`, merges into `actionmaps.xml` (backup first, unknown nodes
  kept, warns while the game runs, confirmation shows what will be written).
- **Settings** – the Star Citizen session to deploy to and read from (found automatically, or pick a folder by
  hand), re-reading the action list, start with Windows, what the window's X does (ask / keep running in the tray /
  exit), and restoring `actionmaps.xml` backups.

## Build and run

Requires the .NET 10 SDK.

```powershell
dotnet build
dotnet test
dotnet run --project src/ScMapper.App
# distributable folder (needs the .NET 10 Desktop Runtime on the target PC)
dotnet publish src/ScMapper.App -c Release -r win-x64 --self-contained false -o dist
```

Settings, profiles and backups live in `%APPDATA%\StarButton`.

## Layout

| Path | Purpose |
| --- | --- |
| `src/ScMapper.Core` | Parser/merge/writer for `actionmaps.xml`, p4k reader, action catalog, matching, rule engine (no UI) |
| `src/ScMapper.Platform` | DirectInput polling, `SendInput` keyboard output |
| `src/ScMapper.App` | WPF UI and tray |
| `tests/ScMapper.Tests` | xUnit tests (uses your install and `actionmaps.xml` read-only when present) |
| `tools/screenshot.ps1` | Starts the app with an isolated data folder and saves a screenshot |
| `poc/` | Python probe used for the first format research |

## Notes and limits

- `jsN` follows the DirectInput order (which is what Star Citizen uses). XInput pads do not take a js number.
  If your numbers differ in game, untick "Follow Windows order" on the stick and set the number by hand.
- Activation modes other than the default (and `double_tap`, written as `multiTap="2"`) are written as an
  `activationMode` attribute on the rebind. This is not verified against the game; test it before relying on it.
- Keyboard output uses `SendInput` with scan codes. Games running as administrator need Star Button to run as
  administrator as well.
- Axes are shown but not editable.
