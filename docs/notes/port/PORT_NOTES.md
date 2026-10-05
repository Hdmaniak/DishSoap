# Dishwasher MonoGame/Android port — build notes

This project builds the decompiled `projectDish` game against MonoGame 3.8.5.1 for
`net8.0-android`. Sources are in `GameSource/` (copied from
`managed/decompiled/game`, which is left untouched). `Platform/` holds the
console-service shims.

## Build

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64
```

Result: **0 errors, 8 warnings** (4× `SYSLIB0006` `Thread.Abort` obsolete, 1×
`XA1008` API34-vs-targetSdk35, 3× JAVAC source/target 8 obsolete).

Gotchas:
* `-j` is not a valid MSBuild switch for this SDK (`MSB1001: Unknown switch`).
* `AndroidManifest.xml` must keep `targetSdkVersion="35"`. With `34` the net8
  workload asks for the missing `platforms/android-34` (`XA5207`); only
  `android-35` is installed.
* A plain Debug build uses Fast Deployment. A manually `adb install`-ed APK then
  aborts with `No assemblies found ... Assuming this is part of Fast
  Deployment`. Add `-p:EmbedAssembliesIntoApk=true` to produce an installable
  standalone APK.

## XNA 3.0 → MonoGame transforms applied to `GameSource/`

| Change | Sites |
|---|---:|
| `SpriteBatch.Begin(SpriteBlendMode…)` → `Begin(SpriteSortMode…, BlendState…)` (AlphaBlend/Additive/None→Opaque, `SaveStateMode` dropped) | 159 |
| `GraphicsDevice.SetRenderTarget(0, rt)` → `SetRenderTarget(rt)` | 23 |
| `new RenderTarget2D(…, levels, MultiSampleType, quality)` → MonoGame ctor (`bool mipMap`, `DepthFormat.None`, `MultiSampleCount`, `RenderTargetUsage.DiscardContents`) | 10 |
| `RenderTarget2D.GetTexture()` removed (RenderTarget2D is a Texture2D) | 50 |
| `Thread.CurrentThread.SetProcessorAffinity(5)` deleted | 5 |
| `GraphicsDevice.CreationParameters.Adapter` → `GraphicsDevice.Adapter` | 1 |

Manual edits: `Game1.LoadGraphicsContent(bool)`→`LoadContent()`,
`UnloadGraphicsContent(bool)`→`UnloadContent()`, removed
`FullScreenRefreshRateInHz` assignment, removed the `GamerServicesComponent`
registration, disambiguated a `Color` ctor in `Text.cs`. Console entry point
(`Program.cs`) and `Properties/AssemblyInfo.cs` were not imported.

## Console services (shimmed, not deleted)

`Netplay.cs`, `Leader.cs`, `Achievements.cs`, `Presence.cs` are kept: the
single-player path references `Globals.netPlay`/`Globals.leader`/`Achievements`
in ~300+ places, plus `Gamer`/`Guide`/`SignedInGamer` in UI flow. They now compile
against local shims in `Platform/`:

* `Shim_GamerServices.cs` — one hardcoded signed-in local gamer ("Player",
  `PlayerIndex.One`); achievements/leaderboards/presence are no-ops.
* `Shim_Net.cs` — real `PacketReader`/`PacketWriter`; `NetworkSession` create/
  find/join throw `NotSupportedException`; leaderboards return empty results.
* `Shim_Storage.cs` — `StorageDevice`/`StorageContainer` mapped to the app
  private directory; `StorageContainer.TitleLocation` provided.
* `Shim_EffectCompat.cs` — `Effect.Begin/End`, `EffectPass.Begin/End` extensions
  (`Begin` → `Apply()`), so existing effect blocks compile until shaders are
  re-authored.
* `AndroidContentBootstrap.cs` — extracts APK assets into
  `files/content` and sets the process CWD there so the game's relative
  `File.Open("data/…")`/`StreamReader` calls work on Android.

## First boot (device <device-serial>)

The app installs and boots into managed code:
`MonoGameAndroidGameView` → `Game.Tick` → `Game1.Initialize` → `MainMenu..ctor`,
then:

```
System.IO.DirectoryNotFoundException: Could not find a part of the path
'/data/data/com.recomp.dishwasher/files/content/data/levels.zdx'.
  at projectDish.MainMenu..ctor
  at projectDish.Game1.Initialize
```

**Next blocker: converted assets.** The content-access layer is working (the
relative path now resolves under the extracted-content root), but no raw
`.zdx/.dqx/.dgx` or texture data is packaged yet. This is owned by the
asset-conversion workstream. Remaining graphics work: XNB v2 → MonoGame
conversion and the 19–25 shader re-authoring.

---

## Raw-data packaging + boot iteration (2026-10-02) — see `../../android/notes/boot-iteration.md`

Raw data is now packaged and the boot is verified on device `<device-serial>`.

* **Packaging:** staged into `src/Dishwasher/Assets/` (auto-included as
  `AndroidAsset`, prefix stripped): `Assets/data/**` (303 raw `.zdx/.dqx/.dgx`),
  `Assets/gfx/**` (128 PNG + 3 spritefont JSON from the texture agent),
  `Assets/gfx/maps/maps.zdx`, and `Assets/Resources/*.resources`. The bootstrap
  extracts 459 files to `files/content` and sets CWD there. No csproj change needed
  for the assets.
* **`.resources` fix:** the game uses a *file-based* `ResourceManager` rooted at
  `StorageContainer.TitleLocation + "/Resources"`. `Platform/Shim_Storage.cs` now
  points `TitleLocation` at the extracted content root, so `MainText..ctor` works.
* **Diagnostics:** `Platform/Log.cs` + logcat on `TraceWriteLine`, the `Loader`
  catch (phase number + exception), and `FatalErrorDie`. Previously loader failures
  were swallowed by the no-op `Guide` shim.
* **Boot result:** passes `MainMenu..ctor` (was the `levels.zdx` crash), `Initialize`,
  `LoadContent` (`gfx/xbla`), and loader phases 0–3; stops at **phase 4** on
  `Content.Load<Effect>("fx/poster")` — expected, no `fx/*.xnb` exist yet.
* **Verified one-past-the-shader-wall:** with effects temporarily skipped, phases
  0–18 all pass and the loader stops at **phase 19** on
  `Sound.Initialize()` → `AudioEngine("sfx/sfxproj.xgs")`.
* **APK:** `bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk`, 135.7 MB,
  installs and launches.
* **Next blocker: compiled shaders** (shader agent): produce `fx/<name>.xnb` for the
  19 `Content.Load<Effect>` sites and package at `assets/fx/**`. Then **audio**
  (audio agent): integrate `notes/audio/shim/XactShim.cs` + transcoded assets.

Note: changed `GameSource/` files are logging-only; `Shim_Storage`, `Log`,
`AndroidContentBootstrap`, and `Activity1` are in `Platform/`. No shader/audio code
was authored.

---

## Boot config — landscape lock + intro cut (2026-10-02)

`Activity1.cs` now locks `ScreenOrientation.Landscape` (merged manifest emits
`android:screenOrientation="landscape"`) and hides the system bars. A single
revertable switch `Globals.SkipIntro` (`GameSource/projectDish/Globals.cs:28`,
default `true`) skips the boot logo carousel and the `Intro`/comic strip and
lands in the main menu. Measured time-to-interactive is unchanged (~23–24 s)
because the ~20 s content decode dominates; the logo gate was coincident with
it. Full details, screenshots and revert steps:
`android/notes/boot-config.md`.

---

## FPS counter + working FPS LOCK (2026-10-02)

The Android Settings menu gained **SHOW FPS COUNTER** (persisted `showFps`, default
off; real ~0.75 s sliding-window presented FPS drawn bottom-left with the game's
comic font), and **FPS LOCK** now genuinely caps presentation
(`IsFixedTimeStep`/`TargetElapsedTime` via `Platform/FrameLimiter.cs`, applied at
startup and on change). Measured on device: 30→29–31, 60→59–60, 120→60,
Unlimited→61 (EGL vsync caps at the 60 Hz panel); `playSeconds` advances at the
same wall-clock rate at 30 as at 60, so it is capped, not slow-motion. Old
`android_settings.sav` files without `<showFps>` load as off. New files:
`Platform/FrameLimiter.cs`, `Platform/PerfOverlay.cs`; two marked hooks in
`Game1.cs` (`:403`, `:4422`). Full details, screenshots and revert steps:
`android/notes/fps-counter-limiter.md`.
