# EveDeck

EveDeck is a Windows desktop window manager for EVE Online multibox layouts. It is similar in spirit to Borderless Gaming, but focused on assigning EVE client windows to named slots, applying saved layouts, and focusing one client at a time with safe global hotkeys.

## Safety Policy

This app only manages OS-level window placement, border style, restore, and focus.

It does not:

- Broadcast input.
- Multiplex input.
- Forward gameplay keys.
- Forward mouse clicks.
- Automate gameplay.
- Read or modify EVE client memory.
- Inject DLLs.
- Automate login.
- Store EVE passwords.

The hotkey layer has a hardcoded guard that permits only focus, cycle focus, layout apply, borderless toggle/restore, move active window, and swap active window actions. No hotkey sends keyboard or mouse input to an EVE client.

## Features

- Detects visible top-level `exefile.exe` windows.
- Optional Notepad test-window detection for local acceptance testing.
- Displays title, process id, handle, coordinates, size, and monitor.
- Assigns windows to named account slots (e.g. Main, Alt 1, Hauler, Scout).
- Persists assignments by window title when possible.
- Creates, edits, deletes, duplicates, imports, and exports JSON layout profiles.
- Includes built-in 2x2, stacked, avoid-taskbar, VSR, and overlap presets.
- Captures current assigned window positions into the active profile.
- Applies active profile geometry and optional borderless style to assigned windows.
- Restores captured normal window style.
- Registers global hotkeys with Win32 `RegisterHotKey`.
- Shows monitor bounds, work area, DPI, and Windows scaling estimate.
- Logs detection, hotkeys, layout application, style changes, and errors locally.
- Copies diagnostics to the clipboard.

## Requirements

Install locally for development:

- Windows 10 or Windows 11.
- .NET 8 SDK or newer with Windows Desktop workload.
- Visual Studio 2022, JetBrains Rider, or VS Code with C# Dev Kit.
- EVE Online for real use, or Notepad for the acceptance smoke test.

No admin rights are required for normal use. Windows may prevent focus changes in some foreground-lock situations; the app logs those failures.

## Build

From a Windows terminal:

```powershell
dotnet restore .\EwcEve.sln
dotnet build .\EwcEve.sln
dotnet run --project .\src\EveWindowCommander\EveWindowCommander.csproj
```

Publish a self-contained build:

```powershell
dotnet publish .\src\EveWindowCommander\EveWindowCommander.csproj -c Release -r win-x64 --self-contained true
```

## Settings and Logs

Settings are stored in:

```text
%LOCALAPPDATA%\EveDeck\settings.json
```

Logs are stored in:

```text
%LOCALAPPDATA%\EveDeck\logs
```

## Acceptance Smoke Test

1. Launch four Notepad windows or four EVE clients.
2. Open EveDeck.
3. Leave "Include Notepad test windows" enabled if testing with Notepad.
4. Assign each detected window to slots 1 through 4.
5. Select `3200x1800 VSR 2x2 - four 1600x900 slots`.
6. Click `Apply`.
7. Confirm slot windows move to:
   - Slot 1: `0,0 1600x900`
   - Slot 2: `1600,0 1600x900`
   - Slot 3: `0,900 1600x900`
   - Slot 4: `1600,900 1600x900`
8. Use `Ctrl+Alt+1` through `Ctrl+Alt+4` to focus one assigned window at a time.
9. Close and reopen the app; profiles, assignments, and hotkeys should persist.

## Notes

The app is per-monitor DPI aware and displays physical pixel coordinates from Win32 window rectangles. A setting is available for "use physical pixels" versus scaled logical coordinates, but exact behavior still depends on Windows scaling, monitor DPI, and the target client window.
