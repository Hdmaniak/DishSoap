# Touch controls — The Dishwasher MonoGame/Android port

> **UPDATE (2026-10-03, supersedes the "MENUS ONLY" scope below):** a later
> iteration added an **optional gameplay on-screen gamepad** and a
> **layout editor** in `src/Dishwasher/Platform/TouchGamepadOverlay.cs`,
> selectable in ANDROID SETTINGS as `TOUCH CONTROLS: OFF/ON` (default OFF),
> plus comic `SPEED UP` (hold A) / `SKIP` (tap B) buttons. Menu gestures are
> unchanged. This work is now synced into this repo and is present in the
> rebuilt public release APK. The "Removed / no gameplay touch code" notes
> below describe the earlier, superseded iteration — see
> `docs/PROJECT_STATE.md` §17 for the current state.


**Author:** Touch Input Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (Samsung A52), app `com.recomp.dishwasher`
**Build:** `net8.0-android`, `targetSdkVersion=35`, `EmbedAssembliesIntoApk=true`
**Scope of edits:** `src/Dishwasher/` and `android/notes/` only.

> **SCOPE CORRECTION (final):** the user asked for **touch controls for the
> menus only**. An earlier iteration in this same session also added an
> optional virtual-left-stick for *gameplay movement*; that was out of scope
> and has been **removed**. The current build has **no gameplay touch code**:
> no virtual stick, no gameplay gestures, no gameplay overlay. Touch gestures
> are additionally **gated to menu context** so they cannot affect active
> gameplay at all. See §6 "Removed" for file:line.

---

## 1. Gesture map (menus only)

| Gesture | Synthesised gamepad | Game meaning |
|---|---|---|
| Tap | `Buttons.A` | confirm / select (`character.keyJump`) |
| Swipe up | `Buttons.DPadUp` | cursor up (`keyUp`) |
| Swipe down | `Buttons.DPadDown` | cursor down (`keyDown`) |
| Swipe left | `Buttons.DPadLeft` | left (`keyLeft`) |
| Swipe right | `Buttons.DPadRight` | right (`keyRight`) |
| Two-finger tap | `Buttons.B` | cancel / back (`character.keyGrab`) |
| Android BACK key/gesture | `Buttons.B` | cancel / back |

A **tap** is a press+release with < 45 logical px travel within 400 ms.
A **swipe** fires once when travel exceeds 55 logical px in the dominant axis.
The Android **BACK** button is consumed only while in a menu (see §3).

## 2. How the synthetic pad is created and driven

`Platform/AndroidInputBridge.cs` (extended; the original gamepad bridge is
untouched):

* `InitializeTouchPad()` (line 318) — if slot 0 is empty, creates an
  `AndroidGamePad` for a live Android `InputDevice` (prefers the touchscreen,
  `sec_touchscreen` id 4) via the real constructor, falling back to
  `RuntimeHelpers.GetUninitializedObject` if the constructor throws. It sets
  `_isConnected = true` and stores it at **slot 0**. A real InputDevice is
  required because MonoGame's `PlatformGetState` calls
  `InputDevice.GetDevice(_deviceId)` and disconnects a pad whose device no
  longer exists.
* `SetTouchButtons(Buttons)` (line 368) / `PublishTouch()` (line 381) — write
  `_buttons = hardwareButtons[0] | touchButtons` into slot 0's pad. The game's
  unmodified `GamePad.GetState(0)` call then sees them.
* `_hwButtons[4]` (line 46) tracks physical controller bits separately, so a
  real pad and touch can both drive slot 0 without clobbering each other
  (`RouteKey` line 229, `RouteMotion` line 268).
* `ResolveSlot` (line 125) promotes a hot-plugged physical gamepad into slot 0
  if the synthetic touch pad owns it.

`TouchControls.Tick()` is called once per game frame from a single marked hook
in `Game1.cs:1078` and ages the button pulses (3 frames: press edge, then
release), so each gesture is exactly one clean down/up edge and menus never
auto-repeat.

## 3. Menu-context gate

`TouchControls.MenuContext()` (`Platform/TouchControls.cs:77`) is
`Game1.gameMode != 0 || Globals.paused`. `OnTouch` (line 156) ignores touches
and `HandleBack` (line 134) returns `false` when in active gameplay, so:

* gestures only ever affect menus (and paused/pause menus);
* in gameplay the Android BACK keeps the platform default (finish the
  Activity), exactly as before this feature.

## 4. Coordinate-space finding (measured on device, earlier in session)

Android delivers `MotionEvent`s to `Activity.DispatchTouchEvent` in **physical
window pixels**, not the SurfaceView's logical space. Measured with verbose
logging:

```
[touch] DOWN raw=(600,300)  logical=(400,200)
[touch] DOWN raw=(1200,600) logical=(800,400)
```

The app window spans the full 2400×1080 panel (widescreen cutout mode), and the
game view is scaled 1.5× by `WidescreenConfig`. `raw = logical × 1.5`, so the
handler normalises by `WidescreenConfig.Scale` before applying thresholds.
Swipe direction is scale-invariant; the normalisation keeps the logical
tap/swipe thresholds device-independent. Screenshot taps must use **physical**
coordinates (e.g. `input tap 1200 600`).

## 5. Build result

```
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true
Build succeeded.
    0 Error(s)
```

(only the pre-existing warnings: 4× SYSLIB0006, 1× XA1008, 3× JAVAC obsolete).

## 6. Removed vs kept (file:line)

**Removed (gameplay touch — was out of scope):**

| Item | Was at |
|---|---|
| virtual-left-stick fields/constants (`_stick`, `StickDeadLogical`, `StickRadiusLogical`) | `Platform/TouchControls.cs` |
| `UpdateStick(dx, dy)` and its calls in Down/Move/Up/Cancel | `Platform/TouchControls.cs` |
| stick state (`_hwLeftStick[4]`, `_touchLeftStick`) | `Platform/AndroidInputBridge.cs` |
| `SetTouchState(buttons, leftStick)` stick publishing | `Platform/AndroidInputBridge.cs` |
| `ReapplyTouchState()` after `DispatchGenericMotionEvent` | `Activity1.cs` |

**Kept (menus only):**

| Item | Location |
|---|---|
| `TouchControls` class, pulses, gesture recognition | `Platform/TouchControls.cs:38` |
| tap/swipe/two-finger handling | `Platform/TouchControls.cs:154` |
| menu-context gate | `Platform/TouchControls.cs:77,156` |
| BACK → B (returns handled flag) | `Platform/TouchControls.cs:132` |
| synthetic pad creation + button merge | `Platform/AndroidInputBridge.cs:318,368,381` |
| touch init call | `Activity1.cs:139` |
| BACK interception (consume only in menu) | `Activity1.cs:227` |
| `DispatchTouchEvent` | `Activity1.cs:247` |
| `OnBackPressed` fallback | `Activity1.cs:256` |
| pulse tick hook | `Game1.cs:1078` |

## 7. Verification status (IMPORTANT — read the scope correction)

Device verification **was** performed earlier in this session, on the pre-gate,
pre-stick-removal build, and proved the menu chain by adb-driven touch with no
controller:

* synthetic pad created with no controller:
  `[touch] synthetic pad at slot 0 (device id=4 name='sec_touchscreen')`;
* tap advanced the title and reached the main menu;
* 5 down-swipes moved the cursor **exactly 5 rows** (one move per swipe, no
  runaway) to `ANDROID SETTINGS`;
* tap opened `ANDROID SETTINGS`, taps cycled `FPS LOCK`;
* Android BACK returned to the main menu and the **process survived**
  (`pid before == pid after`);
* pushed further into `SINGLE PLAYER GAME → difficulty → SELECT LEVEL`.

Screenshots for those steps are in `<dev-notes>/proof/touch/`
(`01-title-nocontroller.png` … `12-level-selected.png`).

**However:** the current build (menus-only, with the menu-context gate and the
virtual stick removed) has **not been re-run on the device** — further device
work was explicitly stopped. The gesture synthesis the menu chain depends on is
unchanged by the scope correction, but the honest status is **device
verification pending for the final build**.

Gameplay status: **no gameplay touch controls** (out of scope, removed).

## 8. Pending device cleanup (NOT run — device work stopped)

The session had temporarily disabled Bluetooth (to get a no-controller state)
and had written an `android_settings.sav` / updated profile while testing.
These were **not** restored because all further adb use was stopped. To restore
the user's phone:

```sh
adb="${ADB:-adb}"; s=<device-serial>
# 1. re-enable Bluetooth
$adb -s $s shell svc bluetooth enable
# 2. restore the backed-up saves (md5s: profile 00ae2cad…, settings fa33d521…)
bk=/tmp/opencode/touch/device-backup
$adb -s $s push $bk/profile.sav  /sdcard/profile.sav
$adb -s $s push $bk/settings.sav /sdcard/settings.sav
$adb -s $s shell run-as com.recomp.dishwasher cp /sdcard/profile.sav  files/Documents/TheDishwasher/profile.sav
$adb -s $s shell run-as com.recomp.dishwasher cp /sdcard/settings.sav files/Documents/TheDishwasher/settings.sav
$adb -s $s shell rm /sdcard/profile.sav /sdcard/settings.sav
# 3. remove the test-created Android settings file
$adb -s $s shell run-as com.recomp.dishwasher rm -f files/Documents/TheDishwasher/android_settings.sav
```

## 9. Revert

Delete `Platform/TouchControls.cs`; remove the `TouchControls` calls in
`Activity1.cs` (139, 227-231, 247-250, 256-261) and `Game1.cs:1078`; remove the
subsequent `ReapplyTouchState()` call in `Activity1.DispatchKeyEvent` (~240)
and the `PORT (touch)` additions in `Platform/AndroidInputBridge.cs`
(`InitializeTouchPad`, `SetTouchButtons`, `PublishTouch`, `_hwButtons`,
`_touchButtons`, `_syntheticSlot0`, promotion block). The original gamepad
bridge behaviour is unchanged.
