# Input diagnosis — controller "A" does nothing on the title screen

**Date:** 2026-10-02
**Device:** `<device-serial>` (Samsung SM-A525F / Galaxy A52, Android, API 34/35)
**App:** `com.recomp.dishwasher` / `crc641d1cdd92eb70a339.Activity1`
**Controller:** Xbox Wireless Controller, Bluetooth, vendor `0x045e` / product `0x02fd`,
Android device id `9`, `ControllerNumber: 1`,
`Sources: KEYBOARD | GAMEPAD | JOYSTICK` (numeric `0x1000511`).
**Owner:** Input Diagnostics Specialist.

---

## 0. TL;DR

* **The title screen polls `GamePad.GetState((PlayerIndex)l)` for `l = 0..3`** and advances
  (`Globals.mainPlayerIndex = j`) when `charArray[j].keyStart || charArray[j].keyJump` is set;
  `A` maps to `keyJump` (`Game1.cs:2882-2884`), `Start` maps to both (`Game1.cs:2789-2791`,
  `2803-2823`).
* **The controller is visible to the app** (`InputDevice.GetDeviceIds()` → `id=9`,
  sources `0x1000511`, vendor `0x45E`, product `0x2FD`) and **the game view is focused**
  (`IsFocused=True`). It is **not** an Android-visibility or focus problem.
* **`Microsoft.Xna.Framework.Input.GamePad.Initialize()` is never called by
  MonoGame.Framework.Android 3.8.5.1** (verified in IL: the only reference to the method is
  its own definition). Consequently `GamePad.GetState(i).IsConnected` is **false for every
  index** until an input event lazily registers a device.
* **The `ControllerNumber == 1` "off-by-one" hypothesis is FALSE for 3.8.5.1.** MonoGame
  ignores `ControllerNumber` entirely and assigns the device to the **first free slot**, so
  the pad lands on `PlayerIndex.One` (index 0). Proven on-device: after calling the missing
  `GamePad.Initialize()`, the log shows `P0=CONN`.
* **Fix implemented:** a reflection-based Android→MonoGame bridge (`Platform/AndroidInputBridge.cs`)
  that (a) explicitly invokes the internal `GamePad.Initialize()` at startup and (b) forwards
  `KeyEvent`/`MotionEvent`s received by the Activity into the framework's own internal
  `AndroidGamePad` instance. No game-logic or `GamePad.GetState` call sites were changed.
* **Verified end-to-end without a physical button:** an injected `KEYCODE_BUTTON_A` (`adb shell
  input keyevent 96`, a vendor-0 "Virtual" device that MonoGame itself discards) drives the
  game through the bridge; `mainPlayerIndex` goes `-1 → 0`, the title disappears, and the game
  reaches the main menu (`PLAY STORY / PLAY ARCADE / DISH CHALLENGE / PRACTICE ROOM / BACK`),
  then a save-data dialog and the main menu (screenshots in `input-proof/`).
* **Still needs a human:** one physical `A` press on the Xbox pad to confirm the *real* device
  takes the same path. The Activity's event stream is device-agnostic, so the bridge captures
  physical events identically, but only a physical press can prove that end-to-end.

---

## 1. MonoGame.Framework.Android 3.8.5.1 input implementation

Resolved package (from `obj/project.assets.json`):

```
MonoGame.Framework.Android/3.8.5.1
lib/net8.0-android34.0/MonoGame.Framework.dll
```

Decompiled with `ilspycmd` (dump retained in `/tmp/opencode/mgfull/`). All line numbers below
are lines in that decompiled dump / the documented constants.

### 1.1 Enumeration — `GamePad` (`Microsoft.Xna.Framework.Input.GamePad`)

| Item | Decompiled file / line |
|---|---|
| storage: `private static readonly AndroidGamePad[] GamePads = new AndroidGamePad[4];` | `Input/GamePad.cs:13` |
| `internal static void Initialize()` — `InputDevice.GetDeviceIds()` loop calling `GetGamePad` | `Input/GamePad.cs:421-428` |
| `internal static AndroidGamePad GetGamePad(InputDevice device)` | `Input/GamePad.cs:240-284` |
| `private static GamePadState PlatformGetState(int index, …)` | `Input/GamePad.cs:190-215` |
| `internal static bool OnKeyDown(Keycode, KeyEvent)` | `Input/GamePad.cs:286-305` |
| `internal static bool OnKeyUp(Keycode, KeyEvent)` | `Input/GamePad.cs:307-317` |
| `internal static bool OnGenericMotionEvent(MotionEvent)` | `Input/GamePad.cs:319-386` |
| `private static Buttons ButtonForKeyCode(Keycode)` | `Input/GamePad.cs:388-419` |
| `AndroidGamePad` class (fields `_deviceId`, `_buttons`, `_leftStick`, …) | `Input/AndroidGamePad.cs` |

`GetGamePad` rejects anything that is not a gamepad and then assigns **the first null slot**:

```csharp
// GamePad.cs:240
if (device == null || (device.Sources & 0x401) != 1025)   // 0x401 == SOURCE_GAMEPAD
    return null;
int num = -1;
for (int i = 0; i < GamePads.Length; i++)
{
    var g = GamePads[i];
    if (g != null && g._isConnected && g._deviceId == device.Id) return g;
    if (g != null && !g._isConnected && g._descriptor == device.Descriptor) { /* reuse */ }
    if (g == null) { GamePads[i] = new AndroidGamePad(device); return GamePads[i]; }  // <-- slot i
    if (!g._isConnected && num < 0) num = i;
}
```

**`ControllerNumber` is never read here.** The device goes to index 0 (the only gamepad device
present among the 9 enumerated devices; every other device fails the `SOURCE_GAMEPAD` test).
So a device with `ControllerNumber == 1` maps to **`PlayerIndex.One`, not `PlayerIndex.Two`**.
There is no off-by-one in 3.8.5.1.

`PlatformGetState` returns disconnected until a slot is populated:

```csharp
// GamePad.cs:190
AndroidGamePad androidGamePad = GamePads[index];
if (androidGamePad == null || !androidGamePad._isConnected)
    return GamePadState.Default;            // IsConnected == false
...
return new GamePadState(..., new GamePadButtons(androidGamePad._buttons), ...);  // IsConnected = true
```

(`GamePadState(GamePadThumbSticks, GamePadTriggers, GamePadButtons, GamePadDPad)` sets
`IsConnected = true` — `Input/GamePadState.cs:68`.)

### 1.2 `GamePad.Initialize()` is never called (root defect)

`ilspycmd -il` over the whole assembly shows exactly **one** occurrence of
`GamePad::Initialize` — its own method definition. `AndroidGamePlatform.cs` contains **no**
`GamePad` reference at all. Therefore `GamePads[]` stays all-null at startup and
`IsConnected` is false for all four indices until the game view happens to deliver a key or
motion event. Confirmed on-device (pre-fix), see §4.1.

### 1.3 Event routing and the vendor/product filter

`MonoGameAndroidGameView` (decompiled `MonoGameAndroidGameView.cs`):

```csharp
// 1244
private bool IsKeyboard(InputDevice device) {
    if ((device.Sources & 0x101) == 257 && device.VendorId != 0)
        return device.ProductId != 0;
    return false;
}
// 1260
private bool IsGamePad(InputDevice device) {
    InputSourceType sources = device.Sources;
    if (((sources & 0x401) == 1025 || (sources & 0x1000010) == 16777232) && device.VendorId != 0)
        return device.ProductId != 0;
    return false;
}
// 1282
public override bool OnKeyDown(Keycode keyCode, KeyEvent e) {
    if (IsGamePad(e.Device) && GamePad.OnKeyDown(keyCode, e)) return true;
    ... IsKeyboard(e.Device) && Keyboard.KeyDown(keyCode) ...
}
// 1318/1339
public override bool OnKeyUp(Keycode, KeyEvent)          // GamePad.OnKeyUp
public override bool OnGenericMotionEvent(MotionEvent)   // GamePad.OnGenericMotionEvent
```

So a device is only bridged to `GamePad`/`Keyboard` when the event's `InputDevice` has
`SOURCE_GAMEPAD` or `SOURCE_JOYSTICK` **and non-zero `VendorId`/`ProductId`**. Events that
fail this test are silently dropped by MonoGame (neither `GamePad` nor `Keyboard` sees them).
This is what makes `adb shell input` (a vendor-0 "Virtual" device) invisible to MonoGame —
demonstrated in §4.2.

### 1.4 Key / axis translation

`ButtonForKeyCode` (`GamePad.cs:388`), matching the standard Android keycodes:

| Android keycode | value | XNA `Buttons` |
|---|---:|---|
| `KEYCODE_DPAD_UP/DOWN/LEFT/RIGHT` | 19/20/21/22 | `DPadUp/Down/Left/Right` |
| `KEYCODE_BUTTON_A` | 96 | `A` |
| `KEYCODE_BUTTON_B` | 97 | `B` |
| `KEYCODE_BUTTON_X` | 99 | `X` |
| `KEYCODE_BUTTON_Y` | 100 | `Y` |
| `KEYCODE_BUTTON_L1/R1` | 102/103 | `LeftShoulder`/`RightShoulder` |
| `KEYCODE_BUTTON_L2/R2` | 104/105 | `LeftTrigger`/`RightTrigger` |
| `KEYCODE_BUTTON_THUMBL/THUMBR` | 106/107 | `LeftStick`/`RightStick` |
| `KEYCODE_BUTTON_START/SELECT` | 108/109 | `Start`/`Back` |

`OnGenericMotionEvent` reads `Axis.X/Y` (left stick), `Axis.Z/Rz` (right stick, Y negated) and
`Axis.Brake(23)`/`Axis.Gas(22)` (triggers), and folds trigger-down into
`Buttons.LeftTrigger`/`RightTrigger`.

The installed keylayout is `/system/usr/keylayout/Vendor_045e_Product_02fd.kl` and maps
`key 304 BUTTON_A`, so a physical `A` press is delivered as keycode 96 — i.e. MonoGame's
translation table covers it.

**Does MonoGame need an "AndroidGamePad/Joystick enabled" flag?** No — there is no feature
flag. The only gate is the source/vendor/product check above. The controller passes it:
`sources=0x1000511`, so `sources & 0x401 == 0x401`, `VendorId=0x45E`, `ProductId=0x2FD`.

---

## 2. Where the game actually reads input (title screen)

Project copy: `src/Dishwasher/GameSource/projectDish/`.

| File:line | Code | Role |
|---|---|---|
| `Game1.cs:1099` | `GamePad.GetState(PlayerIndex.One);` | warm-up call each frame |
| `Game1.cs:1249-1256` | `else { for (int l=0;l<4;l++) doCharKeys(l, GamePad.GetState((PlayerIndex)l), l); }` | **menu/title path**: polls all four indices |
| `Game1.cs:2701` | `else` branch of the `gameMode` chain | menu branch; here `Start` → `keyStart` |
| `Game1.cs:2789-2791` | `if (gp.Buttons.Start …) character[n].keyStart = true;` | `Start` advances title |
| `Game1.cs:2803-2823` | `if (gp.Buttons.Start … ) { … if (gameMode==1) character[n].keyJump = true; }` | `Start` also sets `keyJump` in menus |
| `Game1.cs:2882-2884` | `if (gp.Buttons.A …) character[n].keyJump = true;` | **`A` → `keyJump`** (general, incl. menus) |
| `Game1.cs:3114-3118` | `doKeyboard()` → `Keyboard.GetState()` | only runs because `Globals.isX360 == false` (`Globals.cs:2789`) |
| `MainMenu.cs:671-750` | `if (Globals.mainPlayerIndex < 0) { for j 0..3 if (charArray[j].keyStart \|\| charArray[j].keyJump) { … Globals.mainPlayerIndex = j; … } }` | **title screen "press A/start" handler** |
| `MainMenu.cs:5081-5098` | draws `Globals.maintext.pressStartStr` when `mainPlayerIndex < 0` | the on-screen "PRESS (A) TO START!" |
| `Globals.cs:1193` | `public static int mainPlayerIndex = -1;` | title state |
| `Globals.cs:1281` | `public static int[] controllerIdx = new int[2] { 0, -1 };` | gameplay controller mapping |

So the game polls `GamePad.GetState(PlayerIndex.One … Four)` (all four) on the title, and both
`A` (`keyJump`) and `Start` (`keyStart`) advance it. `charArray` is the `character[]` array
passed from `Game1.Update` to `mainMenu.Update` (`Game1.cs:1433`).

Note the coupling to the GamerServices shim: once `mainPlayerIndex = j`, `MainMenu.Update`
looks up `Gamer.SignedInGamers[(PlayerIndex)j]`. `Platform/Shim_GamerServices.cs:102` only
seeds a gamer at `PlayerIndex.One`, so a controller that landed on index 1-3 would bounce back
to `mainPlayerIndex = -1` (title appears to "do nothing"). This is *not* what happens here —
the pad is at index 0 — but it is worth knowing as a secondary trap.

---

## 3. Instrumentation added (all marks `TEMP INPUT DIAGNOSTICS` / `INPUT FIX`)

| File | Change |
|---|---|
| `Platform/InputDiagnostics.cs` **(new)** | `LogAndroidDevices()` (`InputDevice.GetDeviceIds()` + name/sources/vendor/product/controllerNumber); `Tick()` throttled 500 ms/on-change `GamePad.GetState(0..3)` + `Keyboard.GetState()` + `Globals.mainPlayerIndex` + view focus; `LogKeyEvent()` (Activity `DispatchKeyEvent`); `LogMotionEvent()`; a `View.IOnKeyListener` to prove the focused view receives events |
| `Platform/AndroidInputBridge.cs` **(new)** | the fix: reflective `GamePad.Initialize()` + forwarding Activity key/motion events into the framework's `AndroidGamePad` fields |
| `Activity1.cs` | logs devices + attaches view listener; calls `AndroidInputBridge.InitializePads()`; `DispatchKeyEvent`/`DispatchGenericMotionEvent` overrides forward to the bridge then log |
| `Game1.cs:1025` | one line: `Dishwasher.InputDiagnostics.Tick(gameTime);` at the top of `Update` |

Build/install/launch commands are unchanged from `boot-iteration.md` (`EmbedAssembliesIntoApk=true`,
`targetSdkVersion=35`).

---

## 4. Verbatim results

### 4.1 Pre-fix, no button press — pad visible, MonoGame blind

```
10-02 09:16:17.340 I/Dishwasher(28766): [input][devices] Activity1.OnCreate: InputDevice.GetDeviceIds() -> 9 device(s)
10-02 09:16:17.353 I/Dishwasher(28766): [input][devices]   id=9 name='Xbox Wireless Controller' sources=0x1000511 vendor=0x45E product=0x2FD controllerNumber=1 descriptor='c08e460f7a4767933174a9a3a52acaa3444f5c2c'
10-02 09:16:17.823 I/Dishwasher(28766): [input][focus] view.IsFocused=True view.HasFocus=True view.Focusable=True (change)
10-02 09:16:17.835 I/Dishwasher(28766): [input] P0=- | P1=- | P2=- | P3=- || keys=- || mainPlayerIndex=-1  (change)
```

Conclusion: the app process sees the pad, the game view is focused, yet `IsConnected` is
**false for all four player indices**.

### 4.2 Pre-fix, `adb shell input keyevent 96` — Activity gets it, MonoGame drops it

```
10-02 09:17:08.020 I/Dishwasher(28766): [input][key] action=Down keycode=ButtonA(96) repeat=0 devId=-1 devName='Virtual' sources=0x301 vendor=0x0 product=0x0
10-02 09:17:08.118 I/Dishwasher(28766): [input][key] action=Up   keycode=ButtonA(96) repeat=0 devId=-1 devName='Virtual' sources=0x301 vendor=0x0 product=0x0
10-02 09:17:08.388 I/Dishwasher(28766): [input] P0=- | P1=- | P2=- | P3=- || keys=- || mainPlayerIndex=-1
10-02 09:17:09.389 I/Dishwasher(28766): [input] P0=- | P1=- | P2=- | P3=- || keys=- || mainPlayerIndex=-1
```

Conclusion: MonoGame's `IsGamePad`/`IsKeyboard` filters discard vendor-0 devices; `GamePad`
and `Keyboard` both stay empty.

### 4.3 Post-fix, no button press — `GamePad.Initialize()` works

```
10-02 09:19:12.346 I/Dishwasher(29001): [input][devices]   id=9 name='Xbox Wireless Controller' sources=0x1000511 vendor=0x45E product=0x2FD controllerNumber=1 ...
10-02 09:19:12.383 I/Dishwasher(29001): [input][bridge] GamePad.Initialize() invoked=True available=True
10-02 09:19:33.911 I/Dishwasher(29001): [input] P0=CONN btn=[GamePadButtons: A=0, B=0, Back=0, X=0, Y=0, Start=0, ...] A=0 B=0 Start=0 LT=0.00 RT=0.00 LS=(0.00,0.00) RS=(0.00,0.00) | P1=- | P2=- | P3=- || keys=- || mainPlayerIndex=-1
```

**`P0=CONN` — the `ControllerNumber == 1` pad is registered at `PlayerIndex.One`.** Off-by-one
refuted.

### 4.4 Post-fix, injected `A` — focused view receives it, bridge drives the game

```
10-02 09:21:03.607 I/Dishwasher(29217): [input][viewkey] action=Down keycode=ButtonA(96) devId=-1 devName='Virtual' vendor=0x0 product=0x0
10-02 09:21:03.960 I/Dishwasher(29217): [input] P0=CONN btn=[GamePadButtons: A=1, ...] A=1 B=0 Start=0 ... | P1=- | P2=- | P3=- || keys=- || mainPlayerIndex=-1  (change)
```

```
10-02 09:21:16.472 I/Dishwasher(29217): [input] P0=CONN btn=[GamePadButtons: A=1, ...] A=1 ... || mainPlayerIndex=0  (change)
10-02 09:21:25.958 I/Dishwasher(29217): [input] P0=CONN btn=[GamePadButtons: A=0, ...] A=0 ... || mainPlayerIndex=0
```

Screenshots (in `input-proof/`):
* `01-title-before.png` — "PRESS (A) TO START!" with `mainPlayerIndex=-1`
* `02-after-A-save-dialog.png` — after `A`: "NO GAME SAVE DATA FOUND. CREATE NEW SAVE DATA?"
* `03-after-A-main-menu.png` — after a further `A`: main menu (`PLAY STORY / PLAY ARCADE / DISH CHALLENGE / PRACTICE ROOM / BACK`)

Note: the very first injected `A` at `09:21:03` did *not* advance
(`mainPlayerIndex` stayed `-1`) because the title was still in its "CHECKING STORAGE"
phase; subsequent presses worked. That is normal game behaviour, not an input defect —
worth telling whoever does the physical-press test: **press A after "CHECKING STORAGE"
has finished.**

---

## 5. Root cause

1. **Confirmed defect:** MonoGame.Framework.Android 3.8.5.1 never invokes its own
   `GamePad.Initialize()`. On startup `GamePads[]` is empty, so `GamePad.GetState(i).IsConnected`
   is false for `i = 0..3` even with a controller attached (log §4.1). Any code that gates on
   `IsConnected` (e.g. `Globals.FatalErrorDie`, `Game1.cs:1185`, `GuitarHalper.cs:866`) is wrong
   from boot until the first event.
2. **Best-supported explanation for the physical press doing nothing:** the controller's events
   were not reaching MonoGame's `GamePad` state (only the vendor-0 case is directly proven —
   §4.2; the physical device has vendor/product set, so it *should* pass `IsGamePad`, but the
   symptom requires that it did not). Two plausible mechanisms, both fixed by the bridge:
   * Without `Initialize()`, a pad only exists after an event is processed by
     `MonoGameAndroidGameView.OnKeyDown`; anything that interferes with that one lazy event
     (e.g. the event being consumed before the view, or a first press during the
     "CHECKING STORAGE" window) leaves `GamePad` permanently disconnected from the game's view.
   * MonoGame's `IsGamePad`/`IsKeyboard` vendor/product filter drops the event stream entirely
     for some devices — proven for vendor-0.
3. **Refuted hypothesis:** `ControllerNumber == 1` → `PlayerIndex.Two`. MonoGame 3.8.5.1 never
   reads `ControllerNumber`; it uses the first free slot, and on-device the pad is `P0`
   (`PlayerIndex.One`). See §1.1 and §4.3.

---

## 6. Fix implemented

`Platform/AndroidInputBridge.cs` (additive, marked, revertable):

1. **`InitializePads()`** — reflects the internal
   `Microsoft.Xna.Framework.Input.GamePad.Initialize()` and invokes it once at startup, so
   attached controllers are enumerated into `GamePads[]` and report `IsConnected = true`.
   (This alone cannot be the whole fix because of the filter/drop in §5.2, but it is required
   for a correct connected state.)
2. **`RouteKey(KeyEvent)` / `RouteMotion(MotionEvent)`** — called from `Activity1`'s
   `DispatchKeyEvent` / `DispatchGenericMotionEvent` (which are proven to receive events, §4.4).
   They map Android keycodes/axes to `Buttons`/sticks/triggers and write directly into the
   framework's own internal `AndroidGamePad` instance (`_buttons`, `_leftStick`, `_rightStick`,
   `_leftTrigger`, `_rightTrigger`, `_isConnected`) via reflection. `GamePad.GetState()` then
   returns the bridged state to the **unmodified** game code, so no `GamePad.GetState` call
   sites were patched.
   * Slot selection: match by `InputDevice.Id`; otherwise `ControllerNumber - 1`; otherwise
     first free slot. Only gamepad-like devices are accepted (or the adb "Virtual" device,
     `id < 0`, for testing).
3. `Activity1.cs` invokes `InitializePads()` after `SetContentView`, and forwards events to the
   bridge before `base.DispatchKeyEvent`/`base.DispatchGenericMotionEvent` (so MonoGame's own
   view path still runs for devices it accepts; double-writing the same bits is harmless).

**Why this is the most robust option:** it bypasses MonoGame's device filter and lazy
registration entirely, and taps the Activity dispatch layer that is proven to receive the
events. It requires no game-logic change and no re-authoring.

### 6.1 Does the bridge also work for the physical pad?

Yes by construction. `Activity.DispatchKeyEvent` is source-agnostic: the physical Xbox pad's
key events are dispatched through the same path as the injected `Virtual` event (proven to
fire `[input][key]` and `[input][viewkey]`). For the physical device
`sources = 0x1000511` → `IsGamepadLike` passes, `ResolveSlot` matches the pad already placed at
slot 0 by `InitializePads()` (device id 9), and `KEYCODE_BUTTON_A` (96) maps to `Buttons.A`.
Motion events similarly update the sticks/triggers.

---

## 7. How to run / re-verify

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true

adb="${ADB:-adb}"; serial=<device-serial>
$adb -s $serial install -r bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
$adb -s $serial shell am force-stop com.recomp.dishwasher
$adb -s $serial logcat -c
$adb -s $serial shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
# wait for "CHECKING STORAGE" to finish, then inject A (no physical button needed):
$adb -s $serial shell input keyevent --longpress 96
$adb -s $serial logcat -d -v time -s Dishwasher:V | grep -E '\[input\] P0='
# expect: ... A=1 ... mainPlayerIndex=0
```

To test with the **physical** controller: launch, wait for "CHECKING STORAGE" to finish, press
`A` (or `Start`). Expect `[input] P0=CONN … A=1 …` and `mainPlayerIndex=0` in logcat, then the
save-data dialog / main menu.

---

## 8. What still needs a human

* **A single physical `A` press** on the Xbox pad to confirm the real device produces the same
  `[input] P0=CONN … A=1 … mainPlayerIndex=0` sequence. Everything else (pad visibility,
  focus, enumeration→`P0=CONN`, event dispatch, game reaction) is proven without it.
* If a physical press still does nothing after this fix, check `[input][key]`:
  * **No `[input][key]` line at all** → the Activity isn't receiving the pad's events
    (Android/focus/Bluetooth issue outside the app; escalate).
  * **`[input][key]` present but `mainPlayerIndex` stays `-1`** → the game-side edge was missed
    or the press happened during "CHECKING STORAGE"; retry after the screen settles.
  * **`P1=CONN` instead of `P0`** → slot assignment changed; then the
    `Shim_GamerServices` single-gamer-at-`PlayerIndex.One` coupling (§2) would reject it.

---

## 9. Files changed / revert

Added (delete to revert):

* `src/Dishwasher/Platform/InputDiagnostics.cs` — TEMP diagnostics
* `src/Dishwasher/Platform/AndroidInputBridge.cs` — the fix

Modified (remove the marked blocks to revert):

* `src/Dishwasher/Activity1.cs` — diagnostics + bridge hooks (all inside
  `TEMP INPUT DIAGNOSTICS` / `INPUT FIX` comment fences)
* `src/Dishwasher/GameSource/projectDish/Game1.cs` — one `InputDiagnostics.Tick(gameTime)`
  call at `Update` entry (line ~1025)

Proof artifacts: `<dev-workspace>/android/notes/input-proof/` (3 screenshots + 4 raw
logcat captures). No files under `managed/decompiled`, `assets-clean`, `port`,
`rexglue-sdk*`, `android-app`, or `Content/fx` were touched.
