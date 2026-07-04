# EveDeck

**A window layout and focus manager for EVE Online multiboxing.**

EveDeck arranges your EVE clients into pixel-perfect layouts, shows live previews of your
background clients, and switches between characters with a single keypress — all while staying
strictly inside the EVE Online EULA: it is a **window manager only**. One input, one client,
always.

Website & downloads: **[evedeck.space](https://evedeck.space)**

---

## Features

- **Layout profiles** — built-in families with resolution and account-count dropdowns:
  - **Grid** — classic tiled layouts for 2–15 clients
  - **Center Master** — a large centred master surrounded by a ring of preview tiles
  - **Whammy Board** — Press Your Luck style: tile rows top and bottom, master filling the band between
  - **Side Stack** — a column of previews down either edge, master filling the rest
  - **Stacked / 1-Char / Overlap** — plus fully custom profiles
- **On-monitor layout editor** — a full-screen WYSIWYG editor: drag slots to move, drag edges to
  resize, snap to edges/grid, multi-monitor support. What you draw is exactly what you get.
- **Live previews** — high-quality Windows.Graphics.Capture (D3D11) thumbnails of background
  clients in their layout tiles; click or hover to swap.
- **Fast character switching** — global hotkeys to centre any seat, swap with the master,
  focus by screen direction, or follow a named character wherever they've rotated to.
- **Fixed seats (Model A)** — accounts keep their seat, labels never scramble; window positions
  rotate, identities don't.
- **Resolution independent** — profiles scale to any monitor, VSR/DSR virtual resolutions,
  and any Windows display scale. EVE clients are never resized mid-session (no UI re-flow).
- **ESI integration** — character identity and portraits via EVE SSO (PKCE); no passwords, ever.
- **Quality of life** — borderless toggling, active-window frame glow, tray mode,
  auto-apply on client launch, per-profile taskbar avoidance, import/export profiles.

## EULA compliance

EveDeck is designed to comply with the EVE Online EULA's one-input-one-client rule.
It contains **no** input broadcasting or multiplexing, no key/mouse forwarding, no gameplay or
login automation, no game-memory access, and no password storage. Every hotkey action passes a
safety guard that blocks input-forwarding behaviour by construction. See
[app/COMPLIANCE.md](app/COMPLIANCE.md) for the full boundary.

## Getting started

1. Download the latest release from [evedeck.space](https://evedeck.space) (or the Releases page).
2. Unzip and run `EveDeck.exe` — the build is self-contained; no .NET install required.
3. The first-run wizard detects your clients and picks a layout; tune it in the Layouts tab.

## Building from source

Requirements: Windows 10 19041+, .NET 10 SDK.

```powershell
dotnet build .\app\EveDeck.sln          # build
dotnet test  .\app\EveDeck.sln          # run the test suite

# self-contained publish (what releases ship)
dotnet publish .\app\src\EveDeck\EveDeck.csproj `
  -c Release --self-contained -r win-x64 -o .\app\publish
```

The [evedeck.space](https://evedeck.space) website lives in its own repository:
[EveDeck-site](https://github.com/objectless/EveDeck-site).

## License

EveDeck is free software, licensed under the **GNU GPL v3.0** — see [LICENSE](LICENSE).
Copyright © 2026 EveDeck.

The **EveDeck name and logo are not licensed**: forks must use their own branding.
See [TRADEMARKS.md](TRADEMARKS.md).

EVE Online is a trademark of [CCP hf.](https://www.ccpgames.com/) EveDeck is a third-party
tool and is not affiliated with or endorsed by CCP hf.
