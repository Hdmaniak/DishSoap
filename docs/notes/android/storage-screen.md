# Storage screen skipped + first-run save auto-created (2026-10-05)

**Status:** in the working port and in this repo's `Platform/`/documented here; the
GameSource edits live in the decompiled game (not shipped) and are listed below.

## Why

The Xbox 360 build lets the player choose a storage device and asks, on first run,
"NO GAME SAVE DATA FOUND. CREATE NEW SAVE DATA?". On Android there is no user-selectable
storage device: `Platform/Shim_Storage.cs` satisfies the XNA storage-device selector
synchronously. That makes the "CHECKING STORAGE DEVICE" screen dead time, and the
first-run prompt has no sensible way to be answered.

## What

Two flags in `projectDish/Globals.cs` (GameSource — re-apply in your decompile):

```csharp
public static readonly bool SkipStorageCheck = true;   // skip the checking-storage fade
public static readonly bool SkipNewSavePrompt = true;  // auto-create the profile
```

Call sites (also GameSource):

- `MainMenu.cs` — when `Globals.SkipStorageCheck`, jump `deviceLoadFrame` straight past the
  fade and land on the menu; when `SkipNewSavePrompt`, call
  `Globals.player.Read(coop: false, overwrite: true)` (the exact call the player's "yes"
  makes) instead of `DoMessageBox(24)`.
- `Game1.cs` — the same `SkipNewSavePrompt` path in the boot message-box handling, and the
  boot storage gate becomes `(Globals.SkipStorageCheck || Globals.deviceLoadFrame >= 1f)`.
- `MainMenu.cs` options list — the **SELECT STORAGE DEVICE** row is removed, and the rows
  after it are renumbered (RESET 6→5, BACK 7→6).

Saves are unaffected: the same shim, the same container, and the same read/write path are
used; only the waiting UI and the first-run dialog are removed. An existing profile loads
unchanged.

## Verify

- Cold start with no `profile.sav` reaches the main menu with no storage fade and no dialog.
- `profile.sav` exists afterwards and a subsequent launch resumes it.
- `HELP & OPTIONS > SETTINGS` shows no SELECT STORAGE DEVICE row and the rows still select
  the right actions (RESET is row 5, BACK is row 6).

See `PROJECT_STATE.md` §18.
