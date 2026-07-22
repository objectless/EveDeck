# Microsoft Store — Notes for certification

Paste the block below into **Submission options → Notes for certification** in Partner Center. It
covers the `runFullTrust` restricted-capability justification and pre-empts the question a reviewer
is most likely to have about a third-party tool for an online game: whether it automates or
manipulates the game.

Keep this file in sync with `AppxManifest.xml`'s `<Capabilities>` block and `COMPLIANCE.md` — if the
app ever needs another capability, the justification here has to grow with it.

---

## Paste this

EveDeck is a window layout manager for people who run several EVE Online clients at once. It
arranges those windows on screen, switches focus between them with global hotkeys, and shows live
thumbnail previews of the ones in the background.

**Why runFullTrust is required**

The app's entire purpose is manipulating other applications' top-level windows through the Windows
window manager, which is not possible from a partial-trust app. Specifically it uses:

- `EnumWindows` / `GetWindowText` / `GetWindowRect` — to find the running game clients and read their
  current positions.
- `SetWindowPos` / `BeginDeferWindowPos` / `SetWindowLong` — to move, resize, and (optionally) make
  those windows borderless, so they can be arranged into a chosen layout.
- `RegisterHotKey` + a `SetWinEventHook` foreground hook — for system-wide hotkeys that switch which
  client has focus, and to enable/disable those hotkeys based on which app is in the foreground.
- `DwmRegisterThumbnail` — to render live previews of background windows on an overlay window.
- `SetForegroundWindow` / `ShowWindow` — to focus or minimise a client when the user asks.

It also reads the plain-text log files the EVE client itself writes to the user's Documents folder
(to display which in-game solar system each client is in), and calls CCP/Fenris Creations' public
ESI web API with the user's own OAuth token if they choose to link a character.

**What the app deliberately does NOT do**

Because this is a third-party tool for an online game, we want to be explicit that it is a window
manager and not an automation or cheating tool. EveDeck does not, and is architecturally prevented
from:

- Injecting any code or DLL into the game process.
- Reading or writing the game's memory.
- Sending any synthetic keyboard or mouse input to the game, or broadcasting one input to multiple
  clients. Every hotkey action is limited to focusing, moving, resizing, or restyling a window; the
  allowed action list is enforced in code (`SafetyGuard.AllowedActionPrefixes`).
- Modifying, patching, or reading the game's files beyond the log files the game itself writes.
- Cropping or altering the preview of a client — previews always show the whole window, enforced in
  code.

The app collects no personal data, has no accounts, and contains no telemetry or analytics. Privacy
policy: https://evedeck.space/privacy

The source is public and GPL-3.0 licensed: https://github.com/objectless/EveDeck — the compliance
boundary described above is documented in COMPLIANCE.md in that repository.

EveDeck is an independent project and is not affiliated with or endorsed by CCP hf / Fenris
Creations hf, the makers of EVE Online.
