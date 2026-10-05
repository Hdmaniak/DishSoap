# Rumble / vibration routing (2026-10-05)

**Status:** in this repo under `src/Dishwasher/Platform/`; the GameSource call sites (not
shipped) are listed below.

## Why

The game drives vibration through XNA `GamePad.SetVibration(index, left, right)` (four call
sites in `projectDish/Game1.cs`, once per frame per live player). MonoGame's Android
implementation (`GamePad.PlatformSetVibration`) **ignores the motor values** and calls
`Vibrator.Vibrate(500 ms)` unconditionally — a constant buzz — and only from API ≥ 31.

## What

A value-aware engine replaces those call sites with
`Dishwasher.AndroidRumble.Set(index, left, right)`:

- **Routing** (persisted in `android_settings.sav`, format v2):
  `OFF` / `PHONE` / `CONTROLLER` / `BOTH`, default **BOTH** for saves written before v2.
- **Controller sink**
  - **API ≥ 31:** `InputDevice.VibratorManager` (controller-agnostic).
  - **API < 31:** `InputDevice.Vibrator` if present, else the `@hide` Bluetooth HID-host
    output-report path (`Platform/Java/RumbleHidHost.java`, `sendData`) after the standard
    `VMRuntime.setHiddenApiExemptions` bypass. Only the Xbox One model 1708 report is
    implemented (ported from SDL3 `SDL_hidapi_xboxone.c`); DualShock 4 is a marked
    extension point. Requires `BLUETOOTH` + `BLUETOOTH_ADMIN`, scoped to
    `android:maxSdkVersion="30"`.
- **Phone sink:** the Android device `Vibrator`.
- A request of `0` cancels; `OnPause` / `OnStop` / focus-loss call `AndroidRumble.StopAll()`
  so a motor is never left held; the game's own `Settings.norumble` (OFF) overrides
  everything.

## UI

The game's own options row 1 (`HELP & OPTIONS > SETTINGS`) cycles the four modes via
`Platform/VibrationMenu.cs` (a `partial` extension of `projectDish.MainMenu`). The two
GameSource sites are `case 1: CycleVibration(settings);` and
`drawOption(1, VibrationLabel(settings2), …)`.

## Files

- `Platform/AndroidRumble.cs` (new) — engine, sinks, per-player state.
- `Platform/BluetoothHidRumble.cs` (new) — C# facade + HID report table.
- `Platform/Java/RumbleHidHost.java` (new) — HID-host reflection + output report.
- `Platform/VibrationMenu.cs` (new) — options-row UI + cycling.
- `Platform/AndroidSettings.cs` — `vibration` field, `VERSION = 2`, `SetVibration`, `ClampVib`.
- `Activity1.cs` — `Initialize` / `StopAll` hooks.
- `AndroidManifest.xml` — `VIBRATE`, `BLUETOOTH`/`BLUETOOTH_ADMIN` (≤ API 30).

## Known limitation

Controller vibration is first-class only on **Android 12+**. On Android ≤ 11 it is
best-effort (per-controller Bluetooth HID, currently Xbox One-family pads only); the phone
vibrator works regardless.

See `PROJECT_STATE.md` §18.
