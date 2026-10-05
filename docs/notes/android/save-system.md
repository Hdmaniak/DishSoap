# Save / profile system — map, on-device verification, autosave equivalent

**Author:** Save/Profile Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (`$ADB`), app `com.recomp.dishwasher`
**Scope:** edits confined to `src/Dishwasher/` and this notes tree.
Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.

**One-line result:** explicit saving **already worked** in the port before this
task (game-driven `settings.sav` write → cold restart → UI read proved it). The
real gap was that the 360 original only saves at **level completion / menu-save**,
so Android backgrounding/kill lost mid-level progress. Added a revertable
`Activity1.OnPause/OnStop` autosave that calls the game's **own**
`Globals.player.Write()` / `Globals.settings.Write()` — verified on device.

---

## 1. Save-system map (file:line, decompiled ground truth)

### 1.1 Data model — what is saved

| File | Struct | Contents |
|---|---|---|
| `projectDish/Save.cs` | `Save` (profile) | `ecktoPhase`, `expOrbs`, `playSeconds`, `VeryEasyTrack`/`EasyTrack`/`NormalTrack`/`HardTrack` (20 `|`-joined grades each; `HardTrack` is extended), `upgrades` (`|`-joined weapon levels), `flags` (the rest) |
| `projectDish/Settings.cs:12-24` | `Settings.Save` | `rumble`, `blood`, `soundVol`, `bgmVol`, `flags` (control mode 0/1/2) |
| `projectDish/Save2.cs` | `Save2` | legacy `arg` field; constructed in `Player.Write` (`Player.cs:593`) but **never serialized** |

`Save.flags` layout written by `Player.Write` (`Player.cs:659-701`):
`blood|rumble|borderY|sawTut[16]|impaled|clobbered|guitar[12]` `/`
`inventory[...]` `/` `uberFlags[...]` `/` `arcadeScore[...]`.

* **Progress / level unlocks:** `levelTrack[track].grade[]` (`Player.cs:14-33`,
  `getTrackGrade`/`setTrackGrade` `:78/83`). `MainMenu.levAvailable`
  (`MainMenu.cs:3342-3361`) gates a level on `getTrackGrade(difficulty+1, l-1) > -1`.
* **Shop / upgrades:** `upgrade[]`, serialized as `save.upgrades`
  (`Player.cs:649-658`), set by the in-level upgrade menu (`HUD.cs:3435`).
* **Settings:** separate `settings.sav`.
* **High scores:** story grades in the four tracks; arcade scores in
  `save.flags`' last `/`-section (`arcadeScore[]`, `Player.cs:693-700`).
  Xbox LIVE leaderboards (`Leader.cs`) are a separate online system — the shim
  makes `Leader` a no-op (`Platform/Shim_Net.cs`).
* **Achievements:** `achieved[12]` embedded in the 12 tokens after the 20
  tournament grades of `HardTrack` (`Player.cs:413-437` read, `:645-648` write).

### 1.2 Read / write path

Container (XNA Storage) abstraction:

* `Globals.GetContainer(Player)` — `Globals.cs:1704`; spawns
  `ThreadedOpenContainer` (`:1757`) which calls
  `device.OpenContainer("TheDishwasher", false, out …)` (`:1768`) and caches it
  in `globalContainer` (`:1117`); `hasStorageContainer` (`:1119`) is the
  "container valid" flag; `CloseContainer` (`:1786`) disposes and clears it;
  `PlayerChange` (`:1801`) invalidates it.
* **Port shim:** `Platform/Shim_Storage.cs`. `StorageDevice.IsConnected => true`
  (`:13`); `OpenContainer` returns a `StorageContainer` whose `Path` is
  `Environment.SpecialFolder.Personal + "/TheDishwasher"` (`:49-61`).
  `TitleLocation` points at the extracted content root (`:30-37`).
* On this device `Environment.SpecialFolder.Personal` resolves to
  `/data/data/com.recomp.dishwasher/files/Documents`, so the **save dir is
  `/data/data/com.recomp.dishwasher/files/Documents/TheDishwasher/`**.

Profile (`profile.sav`):

* `Player.Read(bool coop, bool overwrite)` — `Player.cs:263`; builds
  `Path.Combine(container.Path, "profile.sav")` (`:341`, `:355`), deserializes
  with `XmlSerializer(typeof(Save))` (`:364-366`). If the file is absent it
  `Reset()`s and — when `overwrite` — `Write()`s a fresh one (`:343-352`),
  returning `READ_NEW` to trigger the "create new profile?" message box.
* `Player.Write()` / `Write(bool coop)` — `Player.cs:553/558`; serializes
  `profile.sav` with `FileMode.Create` + `XmlSerializer` (`:591`, `:702-705`).
  Guarded by `trial`, `deviceFailed`, `device == null`, `!device.IsConnected`,
  `contains == null` (`:560-588`). Exceptions are swallowed into a
  `Guide.BeginShowMessageBox` write-error dialog (`:708-714`).

Settings (`settings.sav`):

* `Settings.Read(Player)` — `Settings.cs:120`; `settings.sav` via `XmlSerializer`
  (`:157-173`), auto-creating with `Reset()+Write()` when missing (`:158-163`).
* `Settings.Write(Player)` — `Settings.cs:48`; writes with `FileMode.Create`
  (`:70`, `:90-93`), ends with `Globals.CloseContainer()` (`:102`).

Device discovery on the game thread: `Game1.tryGetDevice` (`Game1.cs:906-981`) —
`Guide.BeginShowStorageDeviceSelector` (`:921`), `EndShowStorageDeviceSelector`
(`:933`), assign `Globals.player.device` + `hasDevice = true` (`:944-948`),
then `Globals.player.Read(...)` (`:957`) and `Globals.settings.Read(...)` (`:977`).
The shim completes the async selector synchronously and returns a connected
`StorageDevice` (`Platform/Shim_GamerServices.cs:232-260`).

Serialization format: UTF-8 XML produced by .NET `XmlSerializer` over the
`[Serializable]` structs — not binary, not a custom container.

### 1.3 When the original saves — autosave triggers (exact call sites)

The Xbox 360 build is **not** continuously autosaving; it persists at these
moments:

| Trigger | Call site | Routine |
|---|---|---|
| **Story level complete** | `Map.cs:2146-2155` (set grade `:2149-2151`, `Globals.player.Write()` `:2155`) | `Player.Write()` |
| Arcade round complete | `Map.cs:2008-2013` | `Player.Write()` / `coopPlayer.Write(true)` |
| Online arcade end | `HUD.cs:3245` | `Player.Write()` |
| Arena win → **save menu** | `Map.cs:2074-2079` (`needsSave=true`) → `Game1.cs:1977-1981` (`setLevel(5)`) → `MainMenu.cs:2846` "Save and Continue" / `:2854` "Save and Quit" | `Player.Write()` |
| Settings change | back out of options: `MainMenu.cs:1412` and `MainMenu.cs:2745` | `Settings.Write()` |

The `MainMenu.cs:2837-2864` block is the "SAVE AND CONTINUE / SAVE AND QUIT /
CONTINUE WITHOUT SAVING" screen (strings at `:4765-4767`).

**Mid-level quit does NOT save.** Pause → END GAME shows the confirm screen
"ARE YOU SURE? / YOU WILL LOSE ALL PROGRESS SINCE THE START OF THE LEVEL"
(`HUD.cs:1168-1186`, `UpdateConfirmEnd` `HUD.cs:3858-3957`; confirm sets
`pauseSignal = 3`), and `Game1.cs:2231-2282` tears the player down and returns
to the menu **without** calling `Write()`. This is direct evidence that the
original checkpointed only at level boundaries — reproduced on device
(`22-confirm.png`).

So the user's recollection is right in spirit: **autosave exists, but only at
level completion** (plus explicit menu saves). There is no per-checkpoint or
on-background save in the original.

### 1.4 `hasStorageContainer` / `savingDisabled`

* `Globals.deviceFailed` is set when the device selector returns `null`
  (`Game1.cs:939-943`) or when the container thread does not load within 3 s
  (`Globals.cs:1732-1746`).
* When `deviceFailed`, `MainMenu.showDeviceFailed` draws
  "DEVICE FAILED / SAVING DISABLED" (`MainMenu.cs:5027-5059`) and message box 26
  uses the same pair (`MainMenu.cs:5285-5288`, strings in `MainText.cs:214-216`).
* **The port never triggers it:** the shim's `EndShowStorageDeviceSelector`
  always returns a connected device (`Shim_GamerServices.cs:260`) and
  `OpenContainer` never fails, so `deviceFailed` stays `false`. Confirmed on
  device — no "SAVING DISABLED" screen ever appears; the boot path shows
  "CHECKING STORAGE DEVICE…" then proceeds (`25-pause.png`).

---

## 2. Did saving work before my changes? — yes (on-device)

**Baseline files already on the device** (written by the game before this task,
09:19–09:21):

```
files/Documents/TheDishwasher/profile.sav    1006 bytes  md5 be8bbf8a5b9d737ce99c813705064205
files/Documents/TheDishwasher/settings.sav    263 bytes  md5 b66289792aab8640a282335091893dad
```

Both are valid `XmlSerializer` XML (`profile.sav`: `playSeconds=13`, all grades
`-1`, `settings.sav`: `blood=1`, `soundVol=10`, `bgmVol=10`). The container path
is stable across launches.

**Game-driven write** (settings, the only menu path reachable without
completing a level):

1. Settings screen showed `BLOOD: RED` (matches `blood=1`) — `10-settings-before.png`.
2. Pressed A twice → `BLOOD: OFF` — `11-settings-changed.png`.
3. Navigated to BACK / A → the game called `Settings.Write` — `13-after-back.png`.
4. On disk: `<blood>0</blood>`, mtime **13:24** (was `blood=1`, 09:19).

**Cold-restart read-back:**

5. `am force-stop` then relaunch; opened settings again → **`BLOOD: OFF`**
   (`14-settings-after-restart.png`). The value was read from `settings.sav`.

**Profile read-back (UI):** with the same container, a profile was crafted to
carry grade `111` at level 0 (all four tracks). On launch the **SELECT LEVEL**
screen rendered `FOGHORN CAFÉ … 111` and unlocked `IVY TRAIL`, while
`GRAY FORTRESS` stayed locked (`23-selectlevel.png`) — proving `Player.Read`
drives the UI.

**Numeric profile read-back across a cold restart:** after a force-stop, the
game loaded `profile.sav` (`playSeconds=21`), continued to `24`, and autosaved
`24` — i.e. the on-disk counter was read, not reset.

**Conclusion:** the read/write path, container, filenames and XML format all
work. There was no serialization or shim bug to fix.

> Note: all test-session save mutations were reverted; the device now holds the
> original `profile.sav`/`settings.sav` (md5 exactly as above). Snapshots:
> `<dev-notes>/proof/save/saves/`.

---

## 3. Fix applied — Android background autosave

The only missing behaviour was durability on background/kill, so progress made
after the last level completion is not lost when the user swipes the app away.

**New file** `src/Dishwasher/Platform/SaveAutosave.cs` (revertable):

```csharp
public static void Save(string reason)
{
    // re-entrancy guarded; never overwrite before the profile has been read
    if (player == null || player.device == null) return;
    if (Globals.trial || Globals.deviceFailed) return;
    if (!Globals.hasDevice || !Globals.initialRead) return;
    Globals.settings.Write(player);   // the game's own routine
    player.Write();                   // the game's own routine
    coopPlayer?.Write(coop: true);    // when a 2nd local player exists
}
```

**Hook** in `src/Dishwasher/Activity1.cs` (after `OnWindowFocusChanged`):

```csharp
protected override void OnPause() { base.OnPause(); Dishwasher.SaveAutosave.Save("OnPause"); }
protected override void OnStop()  { base.OnStop();  Dishwasher.SaveAutosave.Save("OnStop");  }
```

It calls the game's **existing** `Globals.player.Write()` / `Globals.settings.Write()`
— no new format, no new file, no change to game logic. The `!initialRead` guard
prevents a fresh default `Player` from overwriting a good save if OnPause fires
before the first read. Story campaign autosave on level completion was already
present (`Map.cs:2155`) and is untouched.

### On-device verification of the autosave

```
10-02 13:28:38.116  Dishwasher: [autosave] saved on OnPause playSeconds=21
10-02 13:28:38.486  Dishwasher: [autosave] saved on OnStop  playSeconds=21
```
`profile.sav` changed 13 → 21, mtime 13:28.

After a cold restart + profile read, a later background produced:
```
10-02 13:29:22.741  Dishwasher: [autosave] saved on OnPause playSeconds=24
```
`profile.sav` = 24 — i.e. the file was read (21) and then written (24).

### Restart / kill persistence

* **Cold restart (`am force-stop` + relaunch):** `profile.sav` and
  `settings.sav` persist; settings UI showed the saved value; the numeric
  counter continued from the stored value.
* **Swipe-away / background:** Android delivers `onPause`/`onStop` before the
  task is removed, both of which now run the autosave (log above). Going to
  Recents first always fires `onPause`, so the swipe case is covered.
* **`am force-stop` (process kill):** Android runs **no** managed lifecycle
  callback, so the background hook cannot fire. This is unavoidable; the
  level-completion autosave still protects progress at level boundaries.

---

## 4. Build / deploy

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true        # Build succeeded, 0 Error(s), 8 warnings
adb -s <device-serial> install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
adb -s <device-serial> shell am force-stop com.recomp.dishwasher
adb -s <device-serial> shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
```

`targetSdkVersion=35` preserved. APK built 13:27.

---

## 5. Regression status

* Only two source files changed: `Activity1.cs` and **new**
  `Platform/SaveAutosave.cs`. `find -newermt` confirms it.
* `InputDiagnostics.Enabled = false` and `RenderDiagnostics.Enabled = false`
  (both still off).
* `grep -rl BlendState.AlphaBlend GameSource/` = **0** (straight-alpha map intact).
* No changes to `MatrixTransform`, `fade`, DXT5 alpha, `NonPremultiplied`,
  `AndroidInputBridge`, the lense sampler fix, landscape/immersive, widescreen,
  or the intro. App boots, menus and gameplay render normally, no FATAL in logcat.

---

## 6. Revert instructions

1. Delete `src/Dishwasher/Platform/SaveAutosave.cs`.
2. Delete the `OnPause`/`OnStop` overrides in
   `src/Dishwasher/Activity1.cs` (the block marked `PORT AUTOSAVE`).
3. Rebuild/reinstall.

The original game-level autosave on level completion (`Map.cs:2155`) remains
either way.

---

## 7. Remaining limitations

* Process kill with no lifecycle callback (`am force-stop`, low-memory kill)
  cannot run managed save code; progress since the last level completion is
  lost exactly as on the original.
* Saves live in app-private storage
  (`/data/data/com.recomp.dishwasher/files/Documents/TheDishwasher/`), so they
  are removed with "Clear storage" / uninstall — same as any Android app.
* The autosave uses the game's synchronous `Player.Write()`; on a background
  transition this is a small file write, well within `onPause` budget.
* `Guide`/LIVE leaderboards remain no-ops (`Shim_Net.cs`), so online high-score
  sync is out of scope — local profile grades/arcade scores persist.

---

## 8. Evidence index (`<dev-notes>/proof/save/`)

| File | What |
|---|---|
| `10-settings-before.png` | settings screen, `BLOOD: RED` (matches on-disk `blood=1`) |
| `11-settings-changed.png` | `BLOOD: OFF` after two A presses |
| `13-after-back.png` | back out of settings (game calls `Settings.Write`) |
| `14-settings-after-restart.png` | **cold restart: `BLOOD: OFF` read back from `settings.sav`** |
| `21-exit-sel.png`, `22-confirm.png` | pause → EXIT GAME + "lose all progress since start of level" warning (no mid-level save) |
| `23-selectlevel.png` | **SELECT LEVEL shows grade `111` read from `profile.sav`; level 2 unlocked** |
| `24-selectlevel-after-restart.png` | post-restart menu (grade `111` persisted on disk) |
| `25-pause.png` | "CHECKING STORAGE DEVICE…" — shim satisfying the XNA selector |
| `saves/profile.sav`, `saves/settings.sav` | restored original saves (md5 as §2) |
| `saves/profile.sav.original-before-tests` | the exact pre-test original |
| `saves/profile.sav.grade-111-test` | the crafted profile used for the UI read-back proof |

Logcat proof of the autosave (device `<device-serial>`), excerpt:

```
Dishwasher: [autosave] saved on OnPause playSeconds=21
Dishwasher: [autosave] saved on OnStop  playSeconds=21
Dishwasher: [autosave] saved on OnPause playSeconds=24
Dishwasher: [autosave] saved on OnPause playSeconds=43   (grade-111 profile)
```
