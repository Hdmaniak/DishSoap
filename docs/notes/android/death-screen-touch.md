# Death screen / "you are dead" touch fix (v1.0.1)

**Symptom.** In the public build, dying left touch-only players stuck: the
**YOU ARE DEAD!** screen (`CONTINUE` / `RESTART LEVEL` / `END`) ignored every
touch, so the game could not be continued. A connected gamepad worked, so the
input layer itself was fine.

**Why.** Two independent gaps lined up:

1. **The touch gate turned the screen away.** Menu touches are only accepted
   when `Game1.gameMode != 0` (front-end) or `Globals.paused` (an in-game menu).
   The death screen runs inside the level (`gameMode == 0`) and the HUD forces
   `Globals.paused = false` while it is up, so it was classified as
   *active gameplay*. Active gameplay touches go to the optional on-screen pad,
   which is deliberately disabled while `Globals.uberDeadFrame > 0`
   (`Game1.portTouchPadContext`). Net result: no owner for the touch, and the
   event was discarded.
2. **There were no tap targets.** The port's tap-to-select machinery
   (`MenuTouchTargets`) records the rows drawn by `MainMenu.drawOption`. The
   death list is drawn directly by the HUD, so nothing was recorded — even a
   correctly-routed tap would have had nothing to hit.

**Fix.**

- A tiny platform-neutral helper (`GameSource/projectDish/DeathScreenTouch.cs`,
  our own code) holds the row rectangles for the current frame plus a one-shot
  pending row, exactly like `MenuTouchTargets`.
- The HUD records the rectangle of each row it draws for the death screen and
  for the follow-up **ARE YOU SURE?** box (confirm / cancel). HUD draws straight
  to the logical backbuffer, so unlike the menus these rects need no
  canvas→screen mapping — the touch layer's logical pixels are already the same
  space.
- The touch layer recognises the death state explicitly (`DeathContext()`:
  `gameMode == 0 && Globals.uberDeadFrame > 0`) and routes taps there instead of
  discarding them. A hit parks the row; the HUD consumes it, sets the existing
  selection (`continueSel` / `uberConfirmSel`) and raises the same `keyJump`
  edge a gamepad **A** press produces, so all original confirm logic (sounds,
  transitions, restart/quit paths) is unchanged.
- **Greyed-out `CONTINUE` is not tappable** when no continues remain: the game's
  own guard bounces that selection onto `RESTART LEVEL`, so a tap on a dead row
  would otherwise open a destructive confirm box.
- **No "tap anywhere = A" fallback** on this screen (unlike the title/intro):
  a stray tap must not confirm `END`.
- Taps that miss every row are ignored; the first 0.5 s of the animation (rows
  not drawn yet, input ignored by the game) is inert for the same reason.

**Not changed.** During the death screen the optional on-screen pad stays
hidden (it is gameplay-only); the screen is driven by tapping its rows. The
in-game pause / inventory / shop overlays keep using the on-screen pad.

**Verified.** On a Galaxy A52 (public build, release-signed, updated over v1.0
with data preserved): tapping `CONTINUE` and the `ARE YOU SURE?` rows works;
front-end tap-to-select is unaffected (`logcat -s Dishwasher`: `[touch] death
tap -> row N`, `[touch] tap -> select row N`).

**Note for source builders.** Both halves of this fix live outside this
repository, like the rest of the XNA→MonoGame porting work:

- the **HUD** hook (record one rectangle per drawn row in the death-screen draw
  path; consume the pending row in its update path, set
  `continueSel`/`uberConfirmSel` and raise `keyJump`), and
- the **touch routing** half that recognises the death state and hit-tests the
  rows (`DeathContext()` + a tap branch in `Platform/TouchControls.cs`).

The rectangles are recorded in logical backbuffer pixels — the same units the
touch layer already delivers — so no canvas mapping is involved. `Begin()` must
run once per HUD frame so stale rows never survive; the confirm box must clear
the list and record its own two rows.

