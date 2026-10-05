# Input / Gamepad Plan — SDL3 -> XInput on Android ARM64

Target: **The Dishwasher: Dead Samurai** (Xbox 360, ReXGlue recompilation) on
`aarch64-linux-android`, min API 26.
SDK: ReXGlue v0.10.0 at `<rexglue-sdk>` (read-only).
SDL3 vendored at `thirdparty/sdl3` (commit `8bf3b7215ad9`, SDL 3.x).

This document maps (1) the SDK XInput/input model, (2) the desktop SDL3
integration and exact event->state path, (3) the Android SDL3 backend, and
(4) the concrete port plan + mapping table. All citations are `file:line`.

> Cross-reference: a sibling agent's broader readiness audit lives at
> `android/notes/android-support-audit.md`, with a CMake toolchain at
> `android/android-aarch64.cmake` (NDK r28b, `ANDROID_ABI=arm64-v8a`, `ANDROID_PLATFORM=android-26`).
> The CMake hunk in the patch (`if(ANDROID)` before `if(UNIX ...)`) is
> consistent with that toolchain (it sets `CMAKE_SYSTEM_NAME Android` and
> `CMAKE_SYSTEM_PROCESSOR aarch64`). That audit independently flags the missing
> `SDL_main` / in-library app-creator entry points (its P0 #7), which this
> document addresses in §3.4 and G1/G11.

---

## 0. TL;DR

* The guest reads/writes a byte-exact `X_INPUT_GAMEPAD` / `X_INPUT_STATE`
  (`include/rex/input/input.h:61-76`). Nothing about that struct changes on
  Android — the whole job is to keep feeding the existing `SDLInputDriver`
  from Android's input stack.
* The existing `SDLInputDriver` (`src/input/sdl/sdl_input_driver.cpp`) is
  platform-neutral and **already compiles on Android** (`src/input/CMakeLists.txt:32-36`).
  No new driver is needed; only the platform/entry plumbing is missing.
* **SDLActivity is mandatory.** On Android SDL3's joystick source is 100 %
  JNI-driven from `SDLActivity.java` + `SDLControllerManager.java`; the Linux
  evdev backend is explicitly disabled on Android
  (`thirdparty/sdl3/CMakeLists.txt:2081`). A bare `NativeActivity` / no-Java
  APK delivers **zero** gamepad events.
* The app entry point must export `SDL_main`. ReXGlue's SDL entry file does not
  include `<SDL3/SDL_main.h>`, so today it would export `main` and SDLActivity
  would fail with *"Couldn't find function SDL_main"* (`SDL_android.c:899`).
* Biggest behavioural caveat: Android rumble is hard-capped at **5000 ms per
  call** (`SDL_sysjoystick.c:605`) and only advertised on **API >= 31**
  (`SDLControllerManager.java:244-249`). XInput vibration is "hold until
  changed", so a held rumble must be periodically re-armed (patch included).

---

## 1. SDK input model (host)

### 1.1 The guest-visible XInput structures

`include/rex/input/input.h`

| Symbol | Location | Notes |
|---|---|---|
| `X_INPUT_GAMEPAD` | `input.h:61-70` | `be<uint16_t> buttons; uint8_t left_trigger; uint8_t right_trigger; be<int16_t> thumb_lx/ly/rx/ry;` size **12** |
| `X_INPUT_STATE` | `input.h:72-76` | `be<uint32_t> packet_number; X_INPUT_GAMEPAD gamepad;` size 16 |
| `X_INPUT_VIBRATION` | `input.h:78-82` | two `be<uint16_t>` motor speeds, size 4 |
| `X_INPUT_CAPABILITIES` | `input.h:84-91` | type/sub_type/flags + gamepad + vibration |
| `X_INPUT_KEYSTROKE` | `input.h:94-101` | for `GetKeystroke` |
| `X_INPUT_GAMEPAD_BUTTON` bits | `input.h:35-51` | DPAD 0x1-0x8, START 0x10, BACK 0x20, LTHUMB 0x40, RTHUMB 0x80, LSHOULDER 0x100, RSHOULDER 0x200, **GUIDE 0x400**, A 0x1000, B 0x2000, X 0x4000, Y 0x8000 |

This is the *only* contract the recompiled game reads. Byte order is PowerPC
big-endian (`be<>`), so host code writes host-endian and the `be<>` wrappers
swap on access.

### 1.2 Guest → host entry points (XAM)

`src/kernel/xam/xam_input.cpp`:

* `XamInputGetCapabilities_entry` — `xam_input.cpp:50`
* `XamInputGetCapabilitiesEx_entry` — `xam_input.cpp:72`
* `XamInputGetState_entry` — `xam_input.cpp:95` (the hot path)
* `XamInputSetState_entry` — `xam_input.cpp:119` (rumble)
* `XamInputGetKeystroke_entry` — `xam_input.cpp:136`
* Exports registered via `REX_EXPORT` — `xam_input.cpp:205-211`
* Stubbed XAM variants (`XamInputControl`, `XamInputGetUserVibrationLevel`,
  `XamInputRawState`, …) — `xam_input.cpp:213-234`

User-index normalisation (`xam_input.cpp:108-112`, `:124-128`, `:150-154`):
any `user_index & 0xFF == 0xFF`, or `XINPUT_FLAG_ANY_USER` (bit 30), is pinned
to **user 0**. This is what lets "press Start on any pad" screens work.

### 1.3 Kernel-level XInput/HID (usually unused by games but implemented as stubs)

`src/kernel/xboxkrnl/xboxkrnl_hid.cpp`:

* `HidReadKeys` (`:24`), `HidGetCapabilities` (`:38`),
  `HidGetLastInputTime` (`:45`), `HidReadMouseChanges` (`:54`) — all return
  unsupported/degenerate results; Xbox 360 titles generally use the XAM
  surface instead.
* `__imp__XInputd*` (the raw driver stack: `XInputdGetCapabilities`,
  `XInputdReadState`, `XInputdFFSetRumble`, …) — **all `REX_EXPORT_STUB`**,
  `xboxkrnl_hid.cpp:68-99`. If the title ever falls back to the kernel HID
  driver it will get nothing. (Dead Samurai uses XInput, so this is a
  low-priority gap.)

### 1.4 Host abstraction

`include/rex/input/input_driver.h:30-66` defines `InputDriver` with
`EnumerateDevices`, `GetDeviceState`, `GetDeviceCapabilities`,
`SetDeviceVibration`, `GetDeviceKeystroke`.

`include/rex/input/input_system.h:28-68` + `src/input/input_system.cpp`:

* `GetState` (`input_system.cpp:215-252`) enumerates devices, asks each driver
  for a state, tracks the "active" pad, then `MergeInto`-folds them.
* `SetState` (`input_system.cpp:254-293`) fans rumble out to every pad on the
  user; synthetic devices never satisfy a pad rumble.
* `GetCapabilities` (`input_system.cpp:187-213`) prefers the pad currently in
  the player's hand.
* `CreateDefaultInputSystem` (`input_system.cpp:324-356`) adds: XInput driver
  (Win32 only), **SDL driver** (all non-Win32, incl. Android), MnK driver,
  NOP fallback, and `SlotAssignment`.

Device→user policy: `src/input/device_assignment.cpp:16-38`.
Device ordinal = connection order; `ordinal N` feeds guest user `N` for
`N < kMaxGuestUsers` (4) (`device.h:18`). Synthetic devices always feed user 0.
Ordinals are never recycled on unplug (`input_system.cpp:94-138`).

State fold and deadzones: `src/input/state_merge.cpp:31-55` and
`include/rex/input/state_merge.h:21-22` (`kThumbDeadzone = 7849`,
`kTriggerThreshold = 30`).

---

## 2. Desktop SDL3 integration and the exact event→state path

### 2.1 Where SDL is initialized

Two separate SDL lifecycles exist:

1. **Windowing/UI** — `src/ui/windowed_app_context_sdl.cpp`
   * `SDL_InitSubSystem(SDL_INIT_VIDEO)` — `:53`
   * top-level event watch — `:66`
   * main loop `SDL_WaitEvent` → `ProcessEvent` — `:87-97`, `:99-181`
2. **Gamepad/input** — `src/input/sdl/sdl_input_driver.cpp`
   * driven from `SDLInputDriver::OnWindowAvailable` (`:54-118`) which runs on
     the UI thread via `CallInUIThreadSynchronous`:
     * `SDL_InitSubSystem(SDL_INIT_EVENTS)` — `:60`
     * `SDL_AddEventWatch(WatchEvent, this)` — `:69-89`
     * `SDL_InitSubSystem(SDL_INIT_GAMEPAD)` — `:92`
     * `SDL_AddGamepadMappingsFromFile` (`hid_mappings_file` cvar) — `:99-113`

`CreateDefaultInputSystem` uses `input_backend = "sdl"` by default
(`input_system.cpp:27-28`).

### 2.2 SDL event → XInput state

```
SDL event queue
  └─ SDL_AddEventWatch callback            sdl_input_driver.cpp:69-89
       └─ SDLInputDriver::HandleEvent      sdl_input_driver.cpp:390-408
            └─ pending_events_.push_back   (mutex only, no controller lock)

guest poll of XamInputGetState
  └─ InputSystem::GetState                 input_system.cpp:215
       └─ SDLInputDriver::GetDeviceState   sdl_input_driver.cpp:194
            ├─ QueueControllerUpdate       :671  (SDL_PumpEvents on UI thread)
            └─ DrainAndLock                :410
                 └─ ProcessEventLocked     :423
                      ├─ ADDED             :443  SDL_OpenGamepad + AllocateDeviceId
                      ├─ REMOVED           :477
                      ├─ AXIS_MOTION       :494  -> gamepad axes/triggers
                      └─ BUTTON_DOWN/UP    :527  -> gamepad.buttons bitfield
```

`GetDeviceState` bumps `packet_number` once per change
(`sdl_input_driver.cpp:210-217`) and zeroes the gamepad while the app is not
active (`:219-223`).

Key detail: the event watch accepts any event in
`[SDL_EVENT_JOYSTICK_AXIS_MOTION, SDL_EVENT_FINGER_DOWN)`
(`sdl_input_driver.cpp:77`). `SDL_EVENT_GAMEPAD_* = 0x650…`
(`SDL_events.h:202-205`) sits inside that window, so gamepad events are caught.

### 2.3 Where the raw button/axis mapping lives

* Axis → `X_INPUT_GAMEPAD` — `sdl_input_driver.cpp:494-525`
* Button bitfield — `sdl_input_driver.cpp:527-589` (lookup table `:530-559`)
* Capabilities/guide handling — `sdl_input_driver.cpp:628-669`
* Rumble → SDL — `sdl_input_driver.cpp:227-246`

---

## 3. Android specifics (thirdparty/sdl3)

### 3.1 The Android joystick backend is JNI/Java-driven

`thirdparty/sdl3/CMakeLists.txt:1497-1502` compiles
`src/joystick/android/SDL_sysjoystick.c` and defines `SDL_JOYSTICK_ANDROID`.
The Linux evdev joystick is **excluded** on Android (`:2081`,
`... AND NOT ANDROID`). So there is no direct `/dev/input` path (which would in
any case be blocked by SELinux for a normal app).

Native entry points (called from Java through JNI):

* `Android_OnPadDown/Up` — `SDL_sysjoystick.c:198-236`
* `Android_OnJoy` — `SDL_sysjoystick.c:238-252`
* `Android_OnHat` — `SDL_sysjoystick.c:254-307`
* `Android_AddJoystick` / `Android_RemoveJoystick` — `SDL_sysjoystick.c:309-454`
* `ANDROID_JoystickRumble` — `SDL_sysjoystick.c:593-607`

JNI registration happens only when `SDLActivity` loads the SDL library and
calls `nativeSetupJNI`:

* `SDLActivity_tab[]` / `SDLControllerManager_tab[]` —
  `thirdparty/sdl3/src/core/android/SDL_android.c:215-260`, `:294-341`
* `nativeSetupJNI` registers them — `SDL_android.c:563-581`
* `Android_JNI_PollInputDevices` calls the Java poller — `SDL_android.c:2186-2190`
* `Android_JNI_HapticRumble` — `SDL_android.c:2210-2214`

Java side:

* `SDLActivity extends Activity` (not NativeActivity) — `SDLActivity.java:60`
* `main()` → `nativeRunMain(library, function, arguments)` —
  `SDLActivity.java:254-262`
* `getMainFunction()` returns the literal string **`"SDL_main"`** —
  `SDLActivity.java:283-285`; `getMainSharedObject()` defaults to `libmain.so`
  when no libraries are declared — `SDLActivity.java:268-277`
* Gamepad key events routed to `onNativePadDown/Up` —
  `SDLActivity.java:1481-1492`
* `SDLControllerManager` native decls — `SDLControllerManager.java:30-41`
* Device enumeration `pollInputDevices()` — `SDLControllerManager.java:214-268`
* Axis/hat dispatch `handleMotionEvent()` — `SDLControllerManager.java:324-339`
* Button mask uses standard `KeyEvent.KEYCODE_BUTTON_*` —
  `SDLControllerManager.java:400-442`

### 3.2 Is `SDLActivity` (or a subclass) required? — **Yes.**

There is no fallback:

* The native joystick list is only populated by `Android_AddJoystick`, called
  from `nativeAddJoystick` (`SDL_android.c:1193-1203`) which is only invoked by
  `SDLControllerManager.pollInputDevices()` (`SDLControllerManager.java:264`).
* `ANDROID_JoystickDetect` (`SDL_sysjoystick.c:469-481`) calls
  `Android_JNI_PollInputDevices`, which needs the Java `mControllerManagerClass`
  registered during `nativeSetupJNI`.
* `nativeSetupJNI` only runs inside `SDLActivity`/`SDL` Java initialization
  (`SDL.java:16`, `SDLActivity.java` native setup).
* `nativeRunMain` needs an exported `SDL_main` symbol
  (`SDL_android.c:817-910`, `SDLActivity.java:283-285`).

**Consequences:**
* A pure `NativeActivity` with no Java SDL classes → no joystick events at all.
* A `#define SDL_MAIN_HANDLED` "I have my own main" APK → SDLActivity still
  needs to exist to forward input; only the entry symbol changes.
* Recommended: **subclass `SDLActivity`** (SDL's own documented pattern,
  `docs/README-android.md:174-194`). Ship `org.libsdl.app.SDLActivity` and
  `SDLControllerManager` unmodified (from `android-project/`).

### 3.3 Do standard Xbox controllers work through Android's input stack?

Yes, generically:

* Android exposes controllers as `InputDevice`s with sources `SOURCE_GAMEPAD`
  and/or `SOURCE_JOYSTICK`; `isDeviceSDLJoystick` accepts those —
  `SDLControllerManager.java:111-138`.
* Buttons arrive as `KeyEvent.KEYCODE_BUTTON_A/B/X/Y/L1/R1/L2/R2/THUMBL/THUMBR/
  START/SELECT/MODE/DPAD_*` — mapped 1:1 by `keycode_to_SDL`
  (`SDL_sysjoystick.c:67-171`) onto `SDL_GAMEPAD_BUTTON_*`.
* Sticks/triggers arrive as `MotionEvent` axes; `RangeComparator` normalises the
  messy `AXIS_Z/AXIS_RZ/AXIS_GAS/AXIS_BRAKE/LTRIGGER/RTRIGGER` ordering
  (`SDLControllerManager.java:157-202`) and the axis mask is built from that
  sorted order (`:364-398`).
* VID/PID are read from `InputDevice.getVendorId()/getProductId()`
  (`SDLControllerManager.java:356-362`) and forwarded to
  `Android_AddJoystick` (`SDL_sysjoystick.c:309`), which builds a GUID and lets
  SDL's gamepad mapper pick the X360 layout. So official Xbox 360/One pads
  (VID `0x045E`) work without custom VID/PID tables, *provided the Android
  build actually contains SDL's gamecontroller DB* — see gaps.
* Triggers: if the pad reports `AXIS_LTRIGGER/AXIS_RTRIGGER` or
  `AXIS_GAS/AXIS_BRAKE` the mapping still lands on the SDL trigger axes; if a
  pad only reports digital L2/R2 as `KEYCODE_BUTTON_L2/R2`, SDL maps L2 to
  `MISC1` and R2 to a raw button 16 (`SDL_sysjoystick.c:108-114`) rather than a
  trigger, which then does **not** reach `left_trigger`. That is inherent to
  SDL's Android backend, not ReXGlue.

### 3.4 Main-entry export (`SDL_main`)

`thirdparty/sdl3/include/SDL3/SDL_main.h:179-194` sets `SDL_MAIN_NEEDED` and
`SDL_MAIN_EXPORTED` on Android; `:262-264` then does `#define main SDL_main`.

ReXGlue's SDL entry file `src/ui/windowed_app_main_sdl.cpp` defines
`int main(...)` at `:129-131` but **never includes `SDL_main.h`**, so the
Android binary will export `main`, not `SDL_main`. SDLActivity will print
`nativeRunMain(): Couldn't find function SDL_main` (`SDL_android.c:899`) and the
game never starts. Fix is in the patch.

A second entry-point problem sits immediately after: line `:58` calls
`rex::ui::GetWindowedAppCreator()`, which is only declared when
`XE_UI_WINDOWED_APPS_IN_LIBRARY == 0`. On Android that macro is forced to 1
(`windowed_app.h:27-30`), so the symbol does not exist and the file will not
compile. In-library builds must instead resolve the app from the registry via
`WindowedApp::GetCreator(identifier)` (`windowed_app.h:44-45,153-159`), where
`identifier` is the token the consumer passed to `REX_DEFINE_APP`
(`windowed_app.h:166-175`). The patch adds an Android branch that reads a
consumer-supplied `REX_APP_ID` macro. (Confirmed by the sibling audit, P0 #7.)

---

## 4. End-to-end Android data path (proposed)

```
Physical pad
  │  (Bluetooth / USB)
  ▼
Android framework  KeyEvent / MotionEvent (SOURCE_GAMEPAD|SOURCE_JOYSTICK)
  │
  ▼
SDLActivity.dispatchKeyEvent                    SDLActivity.java:793
SDLActivity key handler -> SDLControllerManager.onNativePadDown/Up
                                                SDLActivity.java:1481-1492
SDLControllerManager.onGenericMotion            SDLControllerManager.java:710
  └─ handleJoystickMotionEvent                  SDLControllerManager.java:324
  └─ pollInputDevices (add/remove)              SDLControllerManager.java:214
  │
  ▼ JNI  (nativeAddJoystick / onNativeJoy / onNativeHat / onNativePadDown)
SDL_android.c:1150-1215
  │
  ▼
Android_OnJoy / Android_OnHat / Android_OnPadDown / Android_AddJoystick
  SDL_sysjoystick.c:198-454
  │  SDL_SendJoystickAxis / SDL_SendJoystickButton
  ▼
SDL event queue  (SDL_EVENT_GAMEPAD_*)
  │
  ▼
SDLInputDriver event watch -> HandleEvent      sdl_input_driver.cpp:69-89, :390
  │
  ▼
X_INPUT_STATE.gamepad (buttons, triggers, sticks)
  │
  ▼
InputSystem::GetState / MergeInto              input_system.cpp:215, state_merge.cpp:31
  │
  ▼
XamInputGetState guest return                  xam_input.cpp:95
```

Rumble is the reverse:

```
Guest XamInputSetState                         xam_input.cpp:119
  └─ InputSystem::SetState                     input_system.cpp:254
       └─ SDLInputDriver::SetDeviceVibration    sdl_input_driver.cpp:227
            └─ SDL_RumbleGamepad                sdl_input_driver.cpp:242
                 └─ ANDROID_JoystickRumble      SDL_sysjoystick.c:593
                      └─ Android_JNI_HapticRumble -> Java vibrator
                        SDL_android.c:2210 / SDLControllerManager.java:523
```

---

## 5. Mapping table

### 5.1 Buttons — `SDL_GamepadButton` → `X_INPUT_GAMEPAD.buttons`

Index order is the SDL enum order (`SDL_gamepad.h:155-180`), matching the
lookup array at `sdl_input_driver.cpp:530-559`.

| idx | `SDL_GAMEPAD_BUTTON_*` | `X_INPUT_GAMEPAD_*` | bit | Notes |
|----:|------------------------|---------------------|-----|-------|
| 0 | SOUTH (A) | `A` | 0x1000 | |
| 1 | EAST (B) | `B` | 0x2000 | |
| 2 | WEST (X) | `X` | 0x4000 | |
| 3 | NORTH (Y) | `Y` | 0x8000 | |
| 4 | BACK | `BACK` | 0x0020 | |
| 5 | GUIDE | `GUIDE` | 0x0400 | **gated by `guide_button` cvar (default off)** |
| 6 | START | `START` | 0x0010 | |
| 7 | LEFT_STICK | `LEFT_THUMB` | 0x0040 | |
| 8 | RIGHT_STICK | `RIGHT_THUMB` | 0x0080 | |
| 9 | LEFT_SHOULDER | `LEFT_SHOULDER` | 0x0100 | |
| 10 | RIGHT_SHOULDER | `RIGHT_SHOULDER` | 0x0200 | |
| 11 | DPAD_UP | `DPAD_UP` | 0x0001 | also from `Android_OnHat` |
| 12 | DPAD_DOWN | `DPAD_DOWN` | 0x0002 | |
| 13 | DPAD_LEFT | `DPAD_LEFT` | 0x0004 | |
| 14 | DPAD_RIGHT | `DPAD_RIGHT` | 0x0008 | |
| 15 | MISC1 | `GUIDE` | 0x0400 | e.g. L2 digital / share |
| 16 | RIGHT_PADDLE1 | `Y` | 0x8000 | Elite |
| 17 | LEFT_PADDLE1 | `B` | 0x2000 | Elite |
| 18 | RIGHT_PADDLE2 | `X` | 0x4000 | Elite |
| 19 | LEFT_PADDLE2 | `A` | 0x1000 | Elite |
| 20 | TOUCHPAD | `GUIDE` | 0x0400 | PS4/PS5 |

`GUIDE` only reaches the guest when `guide_button` is enabled
(`sdl_input_driver.cpp:580-582`; cvar declared `input_system.cpp:30`,
default `false`).

### 5.2 Axes — `SDL_GamepadAxis` → `X_INPUT_GAMEPAD`

`SDL_GAMEPAD_AXIS_*` order: `SDL_gamepad.h:224-231`.

| SDL axis | target | transform | line |
|---|---|---|---|
| `LEFTX` | `thumb_lx` | `value` (int16, −32768..32767) | `sdl_input_driver.cpp:502-504` |
| `LEFTY` | `thumb_ly` | `~value` (invert: XInput +Y = up) | `:505-507` |
| `RIGHTX` | `thumb_rx` | `value` | `:508-510` |
| `RIGHTY` | `thumb_ry` | `~value` | `:511-513` |
| `LEFT_TRIGGER` | `left_trigger` | `value >> 7` → uint8 0..255 | `:514-516` |
| `RIGHT_TRIGGER` | `right_trigger` | `value >> 7` → uint8 0..255 | `:517-519` |

`Android_OnJoy` scales Android's normalised float `[-1,1]`/`[0,1]` to
`Sint16` via `32767. * value` (`SDL_sysjoystick.c:247`), so the SDL gamepad API
sees `int16` and the shifts above are correct.

### 5.3 X360 quirks to keep in mind

* **Triggers are two separate bytes**, not the combined `LT/RT` bitfield of
  XInput's `wButtons`; a trigger that is only exposed as a digital button on
  some pads will not populate these bytes (see §3.3).
* **Y-axis inversion** uses bitwise `~`, not `-`: SDL rest 0 → XInput `-1`
  (`0xFFFF`). This is the historical Xenia approach and avoids int16 overflow;
  a 1-LSB centre offset is present. Games that require exactly 0 at rest may
  see a hair of upward drift. Consider `-value` with clamping if a title is
  sensitive.
* **Deadzones are *not* applied to the returned state.** `kThumbDeadzone =
  7849` / `kTriggerThreshold = 30` (`state_merge.h:21-22`) are used only by
  `IsNeutral` for active-pad tracking (`state_merge.cpp:46-55`). Real
  `XInputGetState` on the console applies `XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE
  = 7849`, `RIGHT_THUMB_DEADZONE = 8689`, `TRIGGER_THRESHOLD = 30`. If stick
  drift is an issue on Android hardware, add a deadzone/radial-clamp step in
  `OnControllerDeviceAxisMotionLocked` (`sdl_input_driver.cpp:494`).
* **START/BACK/GUIDE** are ordinary bits (0x10/0x20/0x400); nothing special,
  except GUIDE is cvar-gated and MISC1/TOUCHPAD alias to GUIDE.
* `packet_number` increments once per poll when state changed
  (`sdl_input_driver.cpp:210-217`), matching XInput's "changes since last
  call" contract.

---

## 6. Deadzone / rumble / multi-controller handling

### 6.1 Deadzone
Host-side deadzone logic today is limited to neutral detection
(`state_merge.h:21-22`, `state_merge.cpp:25-55`). The analog values handed to
the guest are raw. Recommend a **radial** clamp on the two sticks (preserve the
angle) plus a 30/255 trigger threshold, mirroring XInput, applied in
`sdl_input_driver.cpp` right after `OnControllerDeviceAxisMotionLocked`.

### 6.2 Rumble
* Host path exists and is used: `SDLInputDriver::SetDeviceVibration` →
  `SDL_RumbleGamepad` (`sdl_input_driver.cpp:242`) with
  `kRumbleDurationMs = 0xFFFF` (`:31`).
* **Android caps a single call at 5000 ms** regardless of the requested
  duration (`SDL_sysjoystick.c:605`), so a held vibration silently stops after
  5 s. Fix = re-issue periodically; see patch (`GetDeviceState`).
* **API 26-30: gamepad rumble is not advertised at all.** `can_rumble` is only
  computed inside `if (Build.VERSION.SDK_INT >= 31)`
  (`SDLControllerManager.java:244-249`), so on the min-API-26 target
  `ANDROID_JoystickOpen` will not set the rumble capability
  (`SDL_sysjoystick.c:582-584`) and `SetState` returns failure. Dead Samurai
  uses rumble for combat feedback; plan to either raise min API to 31 for
  rumble-capable titles or add a fallback (generic `Vibrator` / `SDL_HAPTIC`).
* `right_motor_speed`/`left_motor_speed` map to high/low frequency vibrators on
  API 31+ (`SDLControllerManager.java:523-543`); a single-motor device gets a
  0.6/0.4 blend (`:539-542`).

### 6.3 Multi-controller indexing
* Driver order = SDL arrival order; `DeviceId` allocated monotonically
  (`sdl_input_driver.cpp:443-475`, `:618-620`).
* `SDL_SetGamepadPlayerIndex` is set to the connection ordinal for LEDs
  (`sdl_input_driver.cpp:470-474`), but on Android
  `ANDROID_JoystickSetDevicePlayerIndex` is a no-op (`SDL_sysjoystick.c:550-552`).
* InputSystem computes a stable cross-driver ordinal and never recycles it
  (`input_system.cpp:94-138`); `SlotAssignment` maps ordinal N → guest user N
  (`device_assignment.cpp:23-29`).
* Net: first pad connected = player 1 (user 0); up to 4 pads by
  `kMaxGuestUsers = 4` (`device.h:18`). Dead Samurai is single-player + local
  co-op, so this is sufficient.
* Because `guide_button` and the MnK synthetic device both target user 0,
  keyboard/mouse emulation merges into player 1 only; it cannot steal a pad
  slot (`input_system.cpp:113-138`, `:119-123`).

---

## 7. Missing SDK pieces / gaps to implement

| # | Gap | Evidence | Severity |
|---|-----|----------|----------|
| G1 | No `SDL_main` export on Android | `windowed_app_main_sdl.cpp:19-23`, `:129-131` vs `SDL_main.h:262-264`, `SDL_android.c:899` | **Blocker** |
| G11 | Android entry calls `GetWindowedAppCreator()`, which is not declared in the in-library app model Android forces | `windowed_app_main_sdl.cpp:58` vs `windowed_app.h:27-30,176-183` | **Blocker** |
| G2 | No Android entry in the build system; `ANDROID` is not handled by ReXGlue's CMake and falls into the `UNIX` branch | `CMakeLists.txt:152-181` | **Blocker** |
| G3 | No Android app shell (manifest + `SDLActivity` subclass) in-tree | `thirdparty/sdl3/android-project/` is only a template | **Blocker** |
| G4 | No SDL3 Android build wiring (build SDL3 for the NDK, or use the SDL3 Android AAR) | `thirdparty/sdl3/CMakeLists.txt:1430-1614`, `build-scripts/pkg-support/android/aar/` | **Blocker** |
| G5 | `window_sdl.cpp` non-Win/mac branch unconditionally includes X11/Xlib-xcb | `window_sdl.cpp:38-41` | High (build) |
| G6 | Rumble not advertised on API 26-30; Android rumble capped at 5 s | `SDLControllerManager.java:244-249`, `SDL_sysjoystick.c:605` | Medium |
| G7 | No host-side XInput deadzone/radial clamp | `state_merge.h:21-22` used only for neutral | Medium |
| G8 | Kernel `XInputd*`/`Hid*` paths are stubs | `xboxkrnl_hid.cpp:24-99` | Low (title uses XAM) |
| G9 | Guide button disabled by default | `input_system.cpp:30`, `sdl_input_driver.cpp:580-582` | Low |
| G10 | No periodic rumble re-arm for Android's 5 s expiry | `sdl_input_driver.cpp:227-246` | Medium |

Note: `src/input/xinput/xinput_input_driver.cpp` is Windows-only
(`CMakeLists.txt:32-36`) and includes `<xinput.h>`; it must stay excluded on
Android — it already is.

---

## 8. Top risks

1. **Silent "no gamepad" on device.** If the APK is built without the SDL Java
   classes (or the manifest points at a `NativeActivity`), SDL reports zero
   gamepads. Mitigation: start from `thirdparty/sdl3/android-project/`, keep
   `SDLActivity`/`SDLControllerManager`, subclass `SDLActivity`, and log
   `SDL_GetNumGamepads()` at startup.
2. **`SDL_main` symbol mismatch** (G1) — SDLActivity fails to find the entry
   point; looks like an instant crash-to-black. Related: the entry file will
   not even compile on Android because `GetWindowedAppCreator()` is unavailable
   in the in-library app model (G11).
3. **SDL gamecontroller DB absent.** `SDL_OpenGamepad` needs a mapping to
   produce `SDL_EVENT_GAMEPAD_*`. On desktop the driver loads
   `gamecontrollerdb.txt` from a file path (`sdl_input_driver.cpp:99-113`,
   cvar `hid_mappings_file`). On Android the equivalent DB is compiled into
   SDL3; ensure the Android SDL3 build is not stripping the
   `SDL_gamepad_db.h`/`SDL_HINT_GAMECONTROLLERCONFIG` data, otherwise Android
   pads enumerate as joysticks only and never produce gamepad events.
   Belt-and-braces: ship `gamecontrollerdb.txt` in assets and point
   `hid_mappings_file` at it, or set `SDL_HINT_GAMECONTROLLERCONFIG` at init.
4. **Bluetooth input latency / event-thread model.** `HandleEvent` may run on
   the Java UI thread (`sdl_input_driver.cpp:390-408` comment) and only buffers
   events; the guest thread drains them. This is already deadlock-safe
   (`:403-406`) but means input is only as fresh as the next guest poll.
5. **Rumble expectations** (G6): API 26-30 titles get no vibration; API 31+
   gets 5 s capped vibration unless re-armed.
6. **min-API-26 vs Vulkan 1.3.** Unrelated to input but coupled: SDL/MoltenVK
   and the graphics path also need Android bring-up; input cannot be validated
   until a window exists (`OnWindowAvailable` is what starts SDL gamepad init).
7. **Synthetic MnK merge.** On Android, physical pads' key events are consumed
   by the SDL joystick path before the window keyboard listener, so MnK should
   not double-map; verify no `KEYCODE_BUTTON_*` leaks into `WindowSDL::
   HandleKeyEvent` (`window_sdl.cpp:511`).

---

## 9. Recommended order of work

1. G2/G4: build SDL3 + ReXGlue for `aarch64-linux-android` (NDK toolchain;
   `android/android-aarch64.cmake`).
2. G3/G1/G11: add Android app shell (subclass `SDLActivity`), export `SDL_main`,
   and resolve the app from the in-library registry (define `REX_APP_ID` to the
   identifier used by the consumer's `REX_DEFINE_APP`).
3. G5: fix `window_sdl.cpp` platform includes so the Android build links.
4. Verify: log `SDL_GetNumGamepads()`, `SDL_GetGamepadType`, button/axis events.
5. G6/G10: rumble re-arm + API-26 fallback.
6. G7: apply XInput deadzones if drift is observed.
7. G9: enable `guide_button` only if the title needs the Guide button.

---

## 10. File:line index (quick reference)

* Guest structs: `include/rex/input/input.h:35-101`
* XAM input: `src/kernel/xam/xam_input.cpp:50,72,95,119,136,205-211,213-234`
* Kernel HID/XInputd stubs: `src/kernel/xboxkrnl/xboxkrnl_hid.cpp:24-99`
* InputSystem: `src/input/input_system.cpp:81-167,187-293,324-356`
* Assignment: `src/input/device_assignment.cpp:16-54`
* Merge/deadzone: `src/input/state_merge.cpp:31-55`, `include/rex/input/state_merge.h:21-22`
* SDL driver init: `src/input/sdl/sdl_input_driver.cpp:46-118`
* SDL event path: `src/input/sdl/sdl_input_driver.cpp:390-589`
* SDL axis/button mapping: `sdl_input_driver.cpp:494-559`
* SDL rumble: `sdl_input_driver.cpp:227-246`
* SDL app loop: `src/ui/windowed_app_context_sdl.cpp:40-97`
* Entry point: `src/ui/windowed_app_main_sdl.cpp:58,129-131`
* In-library app registry: `include/rex/ui/windowed_app.h:27-30,44-45,153-159,166-183`
* App wiring: `src/ui/rex_app.cpp:243-260,311`
* Platform macro: `include/rex/platform.h:35-37`
* ReXGlue CMake: `CMakeLists.txt:152-181`
* Input CMake: `src/input/CMakeLists.txt:32-38`
* SDL Android joystick: `thirdparty/sdl3/src/joystick/android/SDL_sysjoystick.c:198-454,593-607`
* SDL JNI: `thirdparty/sdl3/src/core/android/SDL_android.c:215-341,817-910,2186-2214`
* SDL Java: `.../SDLActivity.java:60,254-285,793,1481-1492`; `.../SDLControllerManager.java:30-41,214-268,324-339,400-442,523-543`
* SDL main macro: `thirdparty/sdl3/include/SDL3/SDL_main.h:179-194,262-264`
* SDL CMake Android: `thirdparty/sdl3/CMakeLists.txt:1430-1614,1497-1502,2081`
