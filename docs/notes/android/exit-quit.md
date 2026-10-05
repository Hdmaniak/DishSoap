# EXIT really quits (2026-10-05)

**Status:** in this repo under `src/Dishwasher/Platform/PortExit.cs`; the GameSource call
site (not shipped) is listed below.

## Why

The main-menu EXIT row (the game's `"return to arcade"` row, relabelled **EXIT**) mapped to
`Exit()`. MonoGame's Android platform implements `Game.Exit()` as
`MoveTaskToBack(true)` — it only sends the task to the background. Because `Activity1` is
`LaunchMode.SingleInstance` with `AlwaysRetainTaskState`, relaunching returned to that same
live task: the **stale "already loaded" screen** instead of a cold start.

## What

`Platform/PortExit.cs::Quit()`:

1. `SaveAutosave.Save("Exit")` — persists through the game's own save routines
   (`Player.Write()` / `Settings.Write()`), synchronously.
2. `AndroidRumble.StopAll()` — never leave a motor held.
3. `FinishAndRemoveTask()` (API ≥ 21; plain `Finish()` below), then `Process.KillProcess`,
   on the UI thread, so the task cannot be resumed from a cached state.

GameSource: `projectDish/Game1.cs`, `case 4:` calls `Dishwasher.PortExit.Quit();`
(previously `Exit();`). Revert by restoring `case 4: Exit();` and deleting `PortExit.cs`.

The EXIT label comes from `projectDish/MainText.cs`:
`returnToArcadeStr = new StringContainer("exit")` (the main-menu row and its confirmation
dialog both draw this field).

See `PROJECT_STATE.md` §18.
