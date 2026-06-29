# EVE Online EULA / ToS Compliance

> **Not legal advice.** This document records the design constraints that keep EVE Window
> Commander (EWC) on the permitted side of CCP/Fenris Creations' rules. CCP/Fenris Creations' policies can change; for
> certainty about a specific setup, file a support ticket with CCP/Fenris Creations describing the tool.

## The rule that matters

CCP/Fenris Creations' consistent enforcement principle is:

> **One human input may only affect one client.**

In June 2014 (the "Kronos" policy change) CCP/Fenris Creations banned **input broadcasting / multiplexing**
— sending a single keystroke or click to multiple clients at once. This is what made
ISBoxer's broadcasting features bannable. What survived, and remains acceptable, is the
**passive window-management** side: repositioning, resizing, swapping, and arranging client
windows, plus switching focus between them.

## Prohibited categories (EWC must never do these)

1. **Input broadcasting / multiplexing** — one input fanned out to many clients.
2. **Automation / botting** — software that performs in-game actions for the player, reacts
   to game state, or repeats actions on a timer.
3. **Reading or modifying client memory / cache** — memory scraping, packet manipulation,
   reading game data, or altering the EVE client.
4. **Any action performed without a 1:1 human input.**

## What EWC does (all permitted)

EWC stays strictly within the surviving "window management + focus switching" category:

- **Window positioning** — positions / resizes / swaps client windows via Win32
  (`SetWindowPos`, `MoveWindow`). Pure window management.
- **Passive thumbnails** — renders read-only previews of clients via DWM thumbnails / Windows
  Graphics Capture (the same OS capture tech used by OBS and Xbox Game Bar). Video only — no
  game data, no memory access.
- **Focus hotkeys** — global hotkeys that change OS window focus / placement. Each keypress
  affects **exactly one** client (1:1). Centering a seat moves one window to the center and
  parks one off-screen; it never sends input to a client.

## What EWC deliberately does NOT do

- ❌ No input broadcasting. A `SafetyGuard` allow-list hard-blocks any input-broadcast action;
  every hotkey action must be explicitly listed in `SafetyGuard.AllowedActionPrefixes`.
- ❌ No automation / macros that act inside the game.
- ❌ No memory reading, cache scraping, packet inspection, or client modification.
- ❌ No injecting input into EVE clients of any kind.

## Design guardrails (keep it this way)

The compliance risk is **future features**, not the current design. To stay compliant:

- Never add a feature that delivers a single input to more than one client.
- Never add automation that performs or schedules in-game actions.
- Never read EVE process memory or its cache; capture remains video-only (DWM/WGC).
- Any new hotkey action must be added to `SafetyGuard.AllowedActionPrefixes` and must affect
  exactly one client per human input.

## If you need certainty

File a CCP/Fenris Creations support ticket describing EWC as: *"a window manager that positions EVE client
windows and switches focus between them — no input broadcasting, no automation, no memory
access."* That is the description CCP/Fenris Creations has historically permitted.
