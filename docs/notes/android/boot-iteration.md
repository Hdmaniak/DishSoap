# Boot iteration — raw data packaging (Integration & Boot-Iteration Lead)

**Date:** 2026-10-02
**Device:** `<device-serial>` (Android; culture `en-GB`)
**App:** `com.recomp.dishwasher` / `crc641d1cdd92eb70a339.Activity1`
**APK:** `src/Dishwasher/bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk`
**Size:** 135,720,169 bytes (~135 MB), `EmbedAssembliesIntoApk=true`
**Result:** installs, launches, boots to the loader, and stops at the **first compiled
`Effect`** — a shader-agent blocker. Raw/`.resources` packaging is done and verified.

---

## 1. Expected layout vs delivered

The game is console-origin: it reads raw data with **relative `File.Open` /
`StreamReader` paths** rooted at the process working directory, and it loads
converted content through MonoGame's `ContentManager` with
`Content.RootDirectory = "."`.

`Platform/AndroidContentBootstrap.Prepare()` (run in `Activity1.OnCreate` before the
game is constructed) extracts **every APK asset** into the app-private dir

```
/data/user/0/com.recomp.dishwasher/files/content
```

preserving the asset path, then sets `Environment.CurrentDirectory` to that root.
Two consumers therefore have to agree with the APK asset paths:

| consumer | resolved at runtime | needs APK asset at |
|---|---|---|
| `File.Open("data/…")` / `StreamReader("gfx/maps/…")` | `files/content/<relpath>` | `assets/<relpath>` |
| stock `ContentManager` (`RootDirectory="."`) image fallback | `TitleContainer.OpenStream("./gfx/x.png")` → APK assets, and `File.Exists("./gfx/x.png")` → extracted file | `assets/gfx/x.png` |
| `GameResourceManager` file-based `ResourceManager` | `StorageContainer.TitleLocation + "/Resources"` | `assets/Resources/*.resources` |

So the **APK asset layout is identical to the in-game relative paths** (`data/…`,
`gfx/…`, `Resources/…`) — this was confirmed on-device, not assumed.

Exactly what the game reads (from `GameSource/`):

* `data/levels.zdx`, `data/arcade.zdx`, `data/combos.zdx`, `data/bubbles.zdx`,
  `data/pickups.zdx`, `data/tut.zdx`
* `data/maps/*.zdx` (222), `data/strips/*.zdx` (22)
* `data/chars/*.dqx` (38, `CharDef.ReadBinary`, `oldSchoolLoading = true`)
* `data/solos/*.dgx` (15)
* `gfx/maps/maps.zdx` (map segment table, `Map.ReadSegs`)
* `gfx/**` texture PNGs + SpriteFont sidecars (ContentManager fallback)
* `Resources/dishX.Resources.Strings[.culture].resources` (localization)

Case was checked: every `levels.zdx` map path is already lowercase and matches the
`data/maps/*.zdx` file names; the device filesystem is case-sensitive, so this
mattered and is correct.

---

## 2. What was packaged, and how

The .NET 8 Android SDK auto-includes `Assets\**\*` as `AndroidAsset`
(`Microsoft.Android.Sdk.Linux/34.0.154/Sdk/AutoImport.props`), and the `Assets`
prefix is stripped when computing the in-APK asset path. So the packaging is a
staging copy into `src/Dishwasher/Assets/` — **no csproj change needed**:

| staged path | source | files | note |
|---|---|---:|---|
| `Assets/data/**` | `assets-clean/data/**` | 303 | raw `.zdx/.dqx/.dgx` (read-only source) |
| `Assets/gfx/**` | `src/Dishwasher/Content/gfx/**` | 128 PNG + 3 JSON | the texture agent's exported art |
| `Assets/gfx/maps/maps.zdx` | `assets-clean/gfx/maps/maps.zdx` | 1 | raw segment table |
| `Assets/Resources/*.resources` | `assets-clean/Resources/*.resources` | 11 | localized strings (see §5) |

Commands:

```bash
cd src/Dishwasher
mkdir -p Assets/data Assets/gfx Assets/Resources
cp -a $DISHWASHER_ASSETS_DIR/data Assets/data
cp -a Content/gfx/. Assets/gfx/
cp -a $DISHWASHER_ASSETS_DIR/gfx/maps/maps.zdx Assets/gfx/maps/maps.zdx
cp -a $DISHWASHER_ASSETS_DIR/Resources/*.resources Assets/Resources/
```

Verified in the APK:

```
assets/data/levels.zdx           331 B
assets/data/chars/eckto.dqx   136697 B
assets/data/solos/solo_dish0.dgx 1631 B
assets/gfx/maps/maps.zdx        6489 B
assets/gfx/xbla.png            59624 B
assets/gfx/SpriteFont1_JPN.spritefont.json 101899 B
assets/Resources/dishX.Resources.Strings.resources ...
```

At first launch the bootstrap logs `extracted 459 file(s), 0 failure(s)`.

**Staleness caveat:** `Assets/gfx/**` is a copy of another agent's `Content/gfx/`
output. If that output changes, re-run the `cp` above (or switch to `AndroidAsset`
`Link` items). `Assets/data` and `Assets/Resources` are stable read-only source.

---

## 3. Code changes (all additive / diagnostic; no game logic re-authored)

| file | change | why |
|---|---|---|
| `Platform/Log.cs` | **new** Android `Log` helper | loader exceptions were swallowed into a no-op `Guide` message box; without this there is no evidence of the next blocker |
| `Platform/AndroidContentBootstrap.cs` | log extraction counts + per-asset failures; always extract (was silent) | proves extraction succeeded |
| `Platform/Shim_Storage.cs` | `StorageContainer.TitleLocation` now returns `AndroidContentBootstrap.ContentRoot` (fallback to old save dir) | `GameResourceManager` builds `<TitleLocation>/Resources` for its **file-based** `ResourceManager`; the old save-data dir had no resources. Save `Path` is unchanged |
| `Activity1.cs` | `AppDomain.UnhandledException` → logcat | catch native-level crashes |
| `GameSource/projectDish/Game1.cs` | `TraceWriteLine` logs; `Loader` catch logs `loadPhase` + exception | boot milestones + phase attribution |
| `GameSource/projectDish/Globals.cs` | `FatalErrorDie` logs the exception | single choke point for all caught loader failures |

No shader and no audio code was authored or changed. The one temporary diagnostic
(effect loads wrapped in a try/catch to see past the shader wall) was **reverted**;
the checked-in build fails at the real first `Effect`.

---

## 4. Build / install / launch commands

```bash
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true
# -> Build succeeded, 0 errors, 5 warnings (4x SYSLIB0006 Thread.Abort, 1x XA1008)

adb="${ADB:-adb}"; serial=<device-serial>
$adb -s $serial install -r bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
$adb -s $serial shell am force-stop com.recomp.dishwasher
$adb -s $serial logcat -c
$adb -s $serial shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
$adb -s $serial logcat -d -v time -s Dishwasher:V MonoGame:V AndroidRuntime:E
```

---

## 5. Results

### 5.1 `.resources` / localization (fixed here)

`MainText..ctor` calls `GameResourceManager.UseFileBasedResources(typeof(Strings), "Resources")`,
which does `ResourceManager.CreateFileBasedResourceManager("dishX.Resources.Strings",
<StorageContainer.TitleLocation>/Resources, null)` and reflectively swaps the generated
`Strings.resourceMan`. The old shim's `TitleLocation` was the save dir, so lookup threw
`MissingManifestResourceException` at `loadPhase=8`. Embedding the `.resources` in the
assembly did **not** help (the game deliberately uses the file-based manager). Fixing
`TitleLocation` to the extraction root and packaging `assets/Resources/*.resources`
resolved it: `loadPhase=8` now passes.

> Note: the shader wall (phase 4) is *before* this, so the resource fix was proven with
> the temporary effect-skip described above.

### 5.2 FINAL logcat — current wall = first `Effect` (shader agent)

```
10-02 08:37:55.971 I/Dishwasher(26160): bootstrap: extracted 459 file(s), 0 failure(s) into /data/user/0/com.recomp.dishwasher/files/content
10-02 08:37:56.028 I/Dishwasher(26160): [trace] First line
10-02 08:37:56.029 I/Dishwasher(26160): [trace] Device created
10-02 08:37:56.029 I/Dishwasher(26160): [trace] Gamer services component created
10-02 08:37:56.358 D/MonoGame(26160): GraphicsDeviceManager.ResetClientBounds: newClientBounds={X:52 Y:0 Width:975 Height:2168}
10-02 08:37:56.370 I/Dishwasher(26160): [trace] Initialize called
10-02 08:37:56.375 I/Dishwasher(26160): [trace] Point 1 - 374
10-02 08:37:56.376 D/MonoGame(26160): GraphicsDeviceManager.ResetClientBounds: newClientBounds={X:0 Y:124 Width:1080 Height:1920}
10-02 08:37:56.376 I/Dishwasher(26160): [trace] Device settings applied
10-02 08:37:56.376 I/Dishwasher(26160): [trace] Point 2 - 376
10-02 08:37:56.377 I/Dishwasher(26160): [trace] CharDefs created
10-02 08:37:56.377 I/Dishwasher(26160): [trace] Point 3 - 377
10-02 08:37:56.380 I/Dishwasher(26160): [trace] Point 4 - 380
10-02 08:37:56.381 I/Dishwasher(26160): [trace] Point 5 - 381
10-02 08:37:56.383 I/Dishwasher(26160): [trace] Point 6 - 383
10-02 08:37:56.384 I/Dishwasher(26160): [trace] Point 7 - 0
10-02 08:37:56.385 I/Dishwasher(26160): [trace] Loading Graphics content
10-02 08:37:56.399 I/Dishwasher(26160): [trace] Created render targets
10-02 08:37:56.494 I/Dishwasher(26160): [trace] Loaded XBLA texture
10-02 08:37:56.494 I/Dishwasher(26160): [trace] Load Content complete
10-02 08:38:00.525 I/Dishwasher(26160): [loader] failed at loadPhase=4
10-02 08:38:00.584 E/Dishwasher(26160): [loader] Microsoft.Xna.Framework.Content.ContentLoadException: The content file was not found.
10-02 08:38:00.584 E/Dishwasher(26160):  ---> System.IO.FileNotFoundException: Error loading "./fx/poster.xnb". File not found.
10-02 08:38:00.584 E/Dishwasher(26160):    at Microsoft.Xna.Framework.TitleContainer.OpenStream(String name)
10-02 08:38:00.584 E/Dishwasher(26160):    at Microsoft.Xna.Framework.Content.ContentManager.OpenStream(String assetName)
10-02 08:38:00.584 E/Dishwasher(26160):    --- End of inner exception stack trace ---
10-02 08:38:00.584 E/Dishwasher(26160):    at Microsoft.Xna.Framework.Content.ContentManager.OpenStream(String assetName)
10-02 08:38:00.584 E/Dishwasher(26160):    at Microsoft.Xna.Framework.Content.ContentManager.ReadAsset[Effect](String assetName, Action`1 recordDisposableObject)
10-02 08:38:00.584 E/Dishwasher(26160):    at Microsoft.Xna.Framework.Content.ContentManager.Load[Effect](String assetName)
10-02 08:38:00.584 E/Dishwasher(26160):    at projectDish.Game1.Loader()
10-02 08:38:00.587 E/Dishwasher(26160): [FatalErrorDie] ... same ContentLoadException ...
```

This is progressive: `MainMenu..ctor` (the previous `levels.zdx` crash) no longer appears,
`Point 7` is reached, `GraphicsDevice`/`LoadContent` run, and the `Loader` executes
phases 0–3 (raw data, `.dqx`, `maps.zdx`, and `Content.Load<Texture2D>` for
`gfx/text|skastudios|dbp|dish|release|bubbles|controls|controlsh|howtoplay|dishbuy|achievement`) before stopping at `fx/poster`.

App state after failure: process alive, activity resumed (black fail-die frame — the
`Guide` shim message box is a no-op). Screenshot:
`<dev-notes>/proof/boot-effects-wall.png`.

### 5.3 One-past-the-shader-wall run (TEMP diagnostic effect-skip, now reverted)

Wrapping phases 4–5 in a try/catch produced the **next** blocker exactly:

```
10-02 08:36:20.221 I/Dishwasher(25961): bootstrap: extracted 459 file(s), 0 failure(s) ...
10-02 08:36:24.663 E/Dishwasher(25961):  ---> System.IO.FileNotFoundException: Error loading "./fx/poster.xnb". File not found.
10-02 08:36:24.670 E/Dishwasher(25961):  ---> System.IO.FileNotFoundException: Error loading "./fx/bubble.xnb". File not found.
10-02 08:36:41.247 I/Dishwasher(25961): [loader] failed at loadPhase=19
10-02 08:36:41.253 E/Dishwasher(25961): [loader] System.IO.FileNotFoundException: Error loading "sfx/sfxproj.xgs". File not found.
10-02 08:36:41.253 E/Dishwasher(25961):    at Microsoft.Xna.Framework.Audio.AudioEngine.OpenStream(String filePath, Boolean useMemoryStream)
10-02 08:36:41.253 E/Dishwasher(25961):    at Microsoft.Xna.Framework.Audio.AudioEngine..ctor(String settingsFile)
10-02 08:36:41.253 E/Dishwasher(25961):    at projectDish.Sound.Initialize()
```

So phases **0–18** (data, textures, `.dqx` chars, `MainText`/localization, map, head/torso/
legs/weapon/maps atlases) all pass once the effects are skipped, and the loader then
stops at **phase 19 audio** (`Sound.Initialize`).

---

## 6. Blocker attribution / next steps

| phase | blocker | owner | fix needed |
|---|---|---|---|
| 4–5 | `fx/poster`, `fx/bubble`, … `.xnb` missing | **shader agent** | compile the re-authored `.fx` (only 6 `Content/fx/*.fx` exist today; ~19 effect names are requested: poster, lense, grad, blur, bloom, redhaze, fade, water, trainblur, trainover, bubble, burnblur, comicblur, color, trail, newblood, camsplat, ink, wallblood) through MGCB into `Content/fx/*.xnb`, then package/link them as assets at `fx/**` |
| 19–21 | `Sound/MusicSound/VoxSound.Initialize` — Xbox-360 XACT (`sfx/sfxproj.xgs`, big-endian XMA) | **audio agent** | integrate `notes/audio/shim/XactShim.cs` (`using Dishwasher.Audio` in the 10 audio files), build `sfx/cue_map.json`, and package transcoded wav/ogg. Note `<dev-workspace>/Content-sfx/` already has transcoded `ogg/` outputs |
| 8 | `dishX.Resources.Strings` file-based resources | ✅ **fixed here** | `TitleLocation` → extraction root + `assets/Resources/*.resources` |
| — | `XNB v2` textures | ✅ handled by texture agent | `Content/gfx/**` PNGs packaged at `assets/gfx/**`; stock ContentManager PNG fallback loaded `gfx/xbla` and phases 0–3 textures |

**Single next blocker to attack: compiled shaders.** The shader agent should produce
`Content/fx/<name>.xnb` for the 19 `Content.Load<Effect>("fx/<name>")` sites and ensure
they are packaged into the APK at `assets/fx/<name>.xnb` (same staging pattern as
`Assets/gfx`). Once effects load, the next wall is audio integration at `loadPhase=19`.

Not re-authored here (per instructions): shaders and audio. Everything at the
asset/packaging/bootstrap level was done and verified end-to-end.
