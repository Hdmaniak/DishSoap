# Orientation — follow the phone in both landscape directions (The Dishwasher, MonoGame/Android)

**Author:** Orientation Specialist (subagent)
**Date:** 2026-10-03
**Devices:**
* `<device-serial>` — Samsung Galaxy **S24** (SM-S921B), Android **16 / API 36**, panel landscape **2340×1080** — logical **1560×720**, scale **1.5**
* `<device-serial>` — Samsung Galaxy **S9** (SM-G960F), Android **10 / API 29**, effective landscape **2220×1080** (`wm size` override 1080×2220) — logical **1480×720**, scale **1.5**
**App:** `com.recomp.dishwasher` (`crc641d1cdd92eb70a339.Activity1`), `net8.0-android`, `targetSdkVersion=35`, Release + AOT, `EmbedAssembliesIntoApk=true`
**Scope:** edits confined to `src/Dishwasher/` and this notes tree. Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`, `android-app`) were **not** modified.
**Supersedes:** the one-direction `ScreenOrientation.Landscape` lock in `boot-config.md` §1.1; the immersive / widescreen work there and in `widescreen.md` is otherwise unchanged.

---

## 0. TL;DR

| item | result |
|---|---|
| Orientation value | **`ScreenOrientation.UserLandscape`** (was `Landscape`) — both landscape directions allowed, portrait still forbidden |
| Why UserLandscape | respects the user's system auto-rotate / rotation-lock preference (the stated requirement: *"when phone rotation is allowed it should rotate"*); `SensorLandscape` would ignore the lock — not used |
| 180° rotation follows the phone | **yes** — S24 display rotation flips **ROTATION_90 ↔ ROTATION_270**; S9 viewport orientation flips **1 ↔ 3** |
| Upright in both | **yes** — text is upright, no mirroring/inversion in either direction (both devices) |
| Widescreen fill after rotation | **yes** — exact uniform 1.5× fill in both directions (SurfaceFlinger: buffer `1560×720` → display `2340×1080`, `toDisplayTransform scale 1.5`); logical size/scale unchanged |
| Extra code needed | **refactor + a guarded re-apply** — `ApplyWidescreen()` is now idempotent and is re-asserted from `OnConfigurationChanged`; the SurfaceView is **never** re-added (no double-wrap) |
| Rotate-then-tap | **passes** — same physical tap `(450,738)` selects **row 4 (HELP & OPTIONS)** at both ROTATION_90 and ROTATION_270, on both devices |
| Activity recreated? | **no** — PID unchanged across rotations (S24 18129; S9 24312): game loop, audio, FPS limiter and settings state persist |
| Build | **succeeded, 0 errors** (Release + AOT, APK 95,752,045 B) |
| Screenshots | `<dev-notes>/proof/orient/*.png` |
| Limitation | `screencap` normalises the display rotation, so a screenshot alone cannot show the 180° flip — the flip is proven by the `dumpsys` display-rotation values (below); the user's own "auto-rotate ON + physically turn the phone" path was not sensor-injectable on the bench |

---

## 1. The change: `Landscape` → `UserLandscape`

`Activity1.cs` `[Activity]` attribute:

```csharp
// PORT (orientation, 2026-10-03): the game is a 16:9 landscape title.
// `UserLandscape` allows BOTH landscape directions, ...
ScreenOrientation = ScreenOrientation.UserLandscape,   // was ScreenOrientation.Landscape
```

Merged manifest now emits:

```
<activity android:name="crc641d1cdd92eb70a339.Activity1"
          android:configChanges="keyboard|keyboardHidden|orientation|screenSize"
          android:screenOrientation="userLandscape" ...>
```

`ConfigurationChanges` is unchanged (`Orientation`, `ScreenSize`), so the activity is **not** recreated on rotation (verified: same PID across the flip on both devices — game/audio/settings state survive).

### 1.1 Choice and why

* `ScreenOrientation.Landscape` (= `android:screenOrientation="landscape"`) pins **one** landscape direction; rotating the phone 180° leaves the display rotation unchanged, so the image sits upside-down relative to the user. That was the reported bug.
* `UserLandscape` (= `"userLandscape"`, API 18+) allows **both** landscape directions while still disallowing portrait, **and** it honours the user's rotation preference:
  * **auto-rotate ON** → follows the sensor within landscape (the requested behaviour);
  * **rotation locked** → stays at the user's chosen landscape instead of spinning freely (matching "when phone rotation is allowed it should rotate").
* `SensorLandscape` would follow the sensor **even when the user has locked rotation**, which is a behaviour change the user did not ask for. It is kept as the documented fallback only if a device misbehaves with `UserLandscape` (none did here).

MonoGame does **not** override the requested orientation: decompiling `MonoGame.Framework.Android 3.8.5.1` shows `AndroidGameActivity`/`AndroidGameWindow` never call `Activity.RequestedOrientation`; the manifest attribute governs.

---

## 2. Widescreen + immersive across the rotation

### 2.1 What actually happens on a landscape→landscape 180° flip

For a fixed-landscape app the app-local window stays the **same landscape size** (`2340×1080` on the S24; `2220×1080` on the S9) in both display rotations; only the *display rotation* changes (SurfaceFlinger rotates the app's layer). Therefore:

* `Globals.screenSize` / `WidescreenConfig.LogicalWidth|Scale` do **not** need to change;
* MonoGame's GL surface / viewport / render targets stay valid;
* the SurfaceView's `LayoutParams`, `PivotX/Y` and `ScaleX/Y` are app-side View properties that also stay valid.

So a correct implementation needs **no** re-computation of the logical size. The one risk is a vendor build that re-lays-out the window/insets on rotation; to make that safe the setup is now re-assertable.

### 2.2 Refactor (minimal, `// PORT`)

`Activity1.cs`:

* **`OnCreate`** no longer builds the `FrameLayout`/scale inline; it calls `ApplyWidescreen(first: true)`.
* **`ApplyWidescreen(bool first)`** (new) contains the old sizing code and is **idempotent**:
  * computes `physW/physH` from `CurrentWindowMetrics` (API 30+) / `GetRealSize` (else), picks `logicalW = even(round(physW·720/physH))` and `scale = physH/720`;
  * writes `WidescreenConfig` **only on the first call** (`first || !Enabled`). On a re-apply it keeps the size the game already initialised against, and logs if the panel ever reports a different size;
  * creates the `FrameLayout` and `AddView(_view, …)` **only when `_root == null`** — a re-apply never double-wraps or leaks the previous view;
  * re-asserts the layout params in place (only if they differ), then `PivotX/Y = 0`, `ScaleX/Y = scale`.
* **`OnConfigurationChanged`** (new) re-applies cutout mode + `ApplyWidescreen(first: false)` + `HideSystemBars()`, then logs `[orient] …`. Wrapped in `try/catch`.
  * On the **S24 (Android 16)** this callback **does fire** on the flip (`[orient] onConfigurationChanged orientation=Landscape logical=1560x720 scale=1,5`);
  * on the **S9 (Android 10)** it does **not** fire for a landscape→landscape flip (no `[orient]` line), and rotation still worked — i.e. the callback is a safety net, not a requirement.

`_root`, `_logicalW`, `_wsScale` were promoted to fields to support the in-place re-apply.

### 2.3 Verified geometry after rotation

S24 logcat (rotation via `user_rotation` 1 → 3):

```
Dishwasher: [ws] panel=2340x1080 logical=1560x720 scale=1,5 view=0x0 (initial)
Dishwasher: [ws] panel=2340x1080 logical=1560x720 scale=1,5 view=0x0 (re-apply)
Dishwasher: [orient] onConfigurationChanged orientation=Landscape logical=1560x720 scale=1,5
Dishwasher: [trace] [ws] screenSize=1560x720 backbuffer=1560x720 viewport=1560x720 displayMode=1560x720 client=1560x720
```

S9 logcat:

```
Dishwasher: [ws] panel=2220x1080 logical=1480x720 scale=1,5 view=0x0 (initial)
Dishwasher: [trace] [ws] screenSize=1480x720 backbuffer=1480x720 viewport=1480x720 displayMode=1480x720 client=1480x720
```

SurfaceFlinger at **ROTATION_270** (the decisive fill/scale proof — identical structure at ROTATION_90):

```
d04587 SurfaceView[com.recomp.dishwasher/...Activity1]@0(BLAST)
  ... | RGBA_8888 | 0.0 0.0 1560.0 720.0 | 0 0 2340 1080 | ...
  bounds={0,0,1080,2340} toDisplayTransform={ scale x=1.5000 y=1.5000 }
```

`1560 × 1.5 = 2340`, `720 × 1.5 = 1080`, X and Y scale equal ⇒ **exact fill, no bars, no distortion**, in the rotated state.

---

## 3. Touch coordinates across the flip

`TouchControls.Logical(v) = v / WidescreenConfig.Scale` maps the Android window point to logical game pixels; `MenuTouchTargets.HitTest` then maps logical → menu canvas via the exact blit rects `Game1.Draw` recorded. Nothing here is orientation-specific, and Android delivers `MotionEvent`s in the window's (already display-rotated) coordinate space, so the tap point and the rendered rows rotate together.

Verified: the **same** `adb shell input tap 450 738` selects **row 4 (HELP & OPTIONS)** at both rotations on both devices:

```
S24 @ ROTATION_270: [touch] tap -> select row 4 (logical 300,492 physical 450,738)
S24 @ ROTATION_90 : [touch] tap -> select row 4 (logical 300,492 physical 450,738)
S9  @ ROTATION_270: [touch] tap -> select row 4 (logical 300,492 physical 450,738)
S9  @ ROTATION_90 : [touch] tap -> select row 4 (logical 300,492 physical 450,738)
```

The screenshots `S9-tap-rot270.png` / `S9-tap-rot90.png` show the resulting **HELP & OPTIONS** screen (cursor on *HOW TO PLAY*); `NEW-tap-rot90-row4.png` is the S24 equivalent.

---

## 4. On-device verification

**Protocol.** `accelerometer_rotation=0` so the rotation-lock path could be driven deterministically between the two landscape directions (`user_rotation=1` ↔ `3`); this is exactly the preference `UserLandscape` honours. Install with `-r` (data preserved), `force-stop`, `logcat -c`, `am start`, wait for the menu, capture, flip, capture, then tap and read the app's hit-test log. Restore the original rotation settings afterwards.

### 4.1 Rotation actually happens

| device | `user_rotation=1` | `user_rotation=3` |
|---|---|---|
| S24 (Android 16) | `mDisplayRotation=ROTATION_90` | `mDisplayRotation=ROTATION_270` |
| S9 (Android 10) | viewport `orientation=1` | viewport `orientation=3` |

**Before/after (the fix):** with the previously installed `Landscape` build on the S24 the two frames (`OLD-A-userrot1.png`, `OLD-B-userrot3.png`) are the **same** orientation for both `user_rotation` values (a `Landscape` pin allows a single direction); with the new `UserLandscape` build the two `user_rotation` values produce the two distinct display rotations in the table above.

### 4.2 Upright + filled in both directions

| screenshot | what |
|---|---|
| `NEW-A-userrot1.png`, `NEW-B-userrot3.png` | S24 title screen at ROTATION_90 / ROTATION_270 — upright, fills 2340×1080 (measured content bbox `(5,6,2335,1075)`; the 5 px rim is the game's own edge art, present pre-change too) |
| `NEW-menu-rot90.png`, `NEW-menu-rot270.png` | S24 main menu in both directions — upright, full-frame |
| `S9-NEW-A-userrot1.png`, `S9-NEW-B-userrot3.png` | S9 title screen in both directions — upright, fills 2220×1080, FPS counter reads 60 |
| `S9-menu-rot270.png` | S9 main menu |

The FPS counter is visible (−60) in every frame; no status/nav bars appear (immersive survived the flip).

### 4.3 Caveat on screenshots

`adb shell screencap` returns the image in the display's **current** orientation and normalises the physical rotation, so the two directions produce visually identical "upright" frames rather than an upside-down-looking one. The proof that the *phone* drove a 180° flip is the `dumpsys` rotation values in §4.1 plus the compositor geometry in §2.3; the screenshots prove the result is upright and uncropped in both.

### 4.4 No regressions observed

* Same PID across both rotations (S24 `18129`, S9 `24312`) → activity **not** recreated → game loop/audio/settings never torn down.
* No `FATAL` / `AndroidRuntime` / `Unhandled` / `ContentLoadException` in logcat.
* No `AudioTrack` errors after rotation.
* FPS counter/limiter unaffected (counter still 60; `FrameLimiter` untouched).
* Android Settings tree untouched (no edits to `AndroidSettings*`); menu tap-to-select verified above.

---

## 5. Build

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -c Release -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormat=apk
```

**Build succeeded. 0 Error(s)**, 11 pre-existing warnings (`SYSLIB0006`, `CA1422 OnBackPressed`, `CA1416 LongVersionCode`, `XA1008` API34-vs-targetSdk35 — all unrelated/untouched).
APK: `bin/Release/net8.0-android/com.recomp.dishwasher-Signed.apk` — 95,752,045 B, md5 `de4cac894e0f7f36c7d05bc6691396b4`.
Installed on both devices. `targetSdkVersion=35` preserved.

---

## 6. Files changed

* `src/Dishwasher/Activity1.cs`
  * `ScreenOrientation.Landscape` → `ScreenOrientation.UserLandscape` (+ comment).
  * new fields `_root`, `_logicalW`, `_wsScale`.
  * widescreen setup extracted to the idempotent `ApplyWidescreen(bool first)`; called from `OnCreate`.
  * new `OnConfigurationChanged` override (cutout + `ApplyWidescreen(false)` + `HideSystemBars` + `[orient]` log).
* **new** this note + `<dev-notes>/proof/orient/*.png`.
* Rotation settings on both devices restored; S9 save backups in `tools/device-save-backups/orient-20261003/`.

Nothing else touched. In particular: `Game1.cs`, `WidescreenConfig.cs`, `MainMenu.cs`, `MenuTouchTargets.cs`, `TouchControls.cs`, `AndroidInputBridge.cs`, `FrameLimiter.cs`, `AndroidSettings*.cs`, the `.fx` shaders, the blend map, `SaveAutosave.cs`, and `Platform/Shim_Net.cs` are **unchanged**. `RenderDiagnostics.Enabled` and `InputDiagnostics.Enabled` remain `false`.

---

## 7. Limitations

1. **Screenshot normalisation** (see §4.3): the 180° flip is evidenced by `dumpsys` rotation values, not by a single screenshot.
2. **Sensor path not injected.** Testing drove the rotation-lock path (`user_rotation` 1↔3, `accelerometer_rotation=0`), which is the path `UserLandscape` documents. The auto-rotate-ON + physical-turn path uses the same system rotation machinery and is expected to behave identically, but could not be sensor-injected from `adb`.
3. **S24 saves could not be pulled.** The S24 is unrooted and the Release APK is non-debuggable, so `run-as`/`adb backup` cannot read `files/Documents/TheDishwasher/`. The APK is signed with the default debug keystore, so `install -r` updates preserve app data; the test was menu-only (no level completion) and no save was altered. The known-good snapshot `tools/device-save-backups/pre-mp/s24-RFCX31Y16/` is referenced. (The rooted S9 was fully backed up and verified byte-identical afterwards.)
4. **S24 locked itself** (secure keyguard) during the idle test window and requires the user's unlock; the app remains running behind the lock screen. S9 was returned to the launcher.
5. A tap landing during the menu fade-in can hit the row at its *animating* position (tap-to-select records per-frame row rects) — pre-existing touch behaviour, unrelated to orientation; taps on the settled menu hit the expected row (§3).

---

## 8. Revert

* `Activity1.cs`: set `ScreenOrientation = ScreenOrientation.Landscape`; delete the `OnConfigurationChanged` override and the `ApplyWidescreen` refactor (or leave `ApplyWidescreen(first: true)` as the original inline block). The `Landscape` behaviour described in `boot-config.md` returns.
* No other file needs reverting.
