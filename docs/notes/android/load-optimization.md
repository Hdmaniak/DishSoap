# Load / startup optimization — The Dishwasher (MonoGame/Android)

**Owner:** Loading/Startup Optimization Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (Galaxy A52, arm64-v8a, Android 13/targetSdk 35)
**Project:** `src/Dishwasher/`
**Scope:** edits confined to `src/Dishwasher/` and `android/notes/`.
Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.

---

## 0. TL;DR

| metric | before (Debug) | after (final Release) | speed-up |
|---|---:|---:|---:|
| `am start` → first frame | ~2.41 s (3-run mean) | **~0.83 s** warm / ~2.3 s first run after install | ~2.9× |
| `am start` → menu interactive | **~24.6 s** (3-run mean; prior note ~23.3 s) | **~5.0 s** warm / **~6.5 s** cold first run | **~4.9×** |
| boot loader phases 0→25 | ~21.7 s | **~1.5 s** | ~14× |
| PNG decode (91 textures) | 20.5 s (decode 18.4 s + GL upload 2.1 s) | ~1.5 s wall (parallel; decode spread over the pool) | ~13× |
| bootstrap content extraction | ~1.5 s every launch | ~1.5 s once per install, then **0** | — |

The dominant fix was **not** the intro: it was (a) building **Release+AOT**,
which appears to include full AOT by default and cut PNG decode 3.6×, (b)
**parallelising the CPU PNG decode**, and (c) shrinking the **hard-coded
20.45 s boot-carousel gate** that then became the bottleneck. Deferred asset
loading was evaluated and **deliberately not implemented** (see §4.5): the
loader is no longer the critical path, so it would add risk with no end-to-end
gain.

---

## 1. Measurement method (exact and repeatable)

All timings are **relative to the ActivityTaskManager `START` line in logcat**,
not host wall-clock (the device clock is ~2 s off the host). Device `adb` is
`$ADB`, serial `<device-serial>`.

```sh
source tools/scripts/env.sh
adb -s <device-serial> shell am force-stop com.recomp.dishwasher
adb -s <device-serial> logcat -c
adb -s <device-serial> shell am start -W \
    -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1   # -> TotalTime = first frame
# poll logcat until: [boot] content loaded; entering main menu   -> "menu interactive"
adb -s <device-serial> logcat -d -v time
```

* **first frame** = `am start -W TotalTime` (ActivityTaskManager `Displayed`).
* **menu interactive** = the game's own marker
  `[trace] [boot] content loaded; entering main menu`, logged the frame
  `gameMode` becomes 1 (the main menu then accepts input). Verified by driving
  input after the marker (title → menu works, see §6).
* **per-phase breakdown** used temporary loader logging, clearly marked
  `// PORT (perf)`, added for this task and **removed again before the final
  build** (so the shipped APK has zero instrumentation). It logged each
  `Loader()` phase with its duration, plus a per-PNG decode/upload split and the
  bootstrap extraction time.
* **decode vs upload split** was obtained by passing a custom
  `Action<byte[]>` colour processor to `Texture2D.FromStream`: the callback
  fires after StbImageSharp decode and before the GL `SetData`, so
  `entry−start = decode` and `return−entry = upload`.

Baseline was 3 runs; every retained change was measured with at least one run
and, where it mattered, repeated. Raw logs are quoted inline; the boot was
captured with screenshot bursts (`android/notes/load-optimization/`).

---

## 2. Baseline breakdown (Debug, `SkipIntro=false`, before any change)

3-run means (runs: `baseline-r1/r2/r3`):

| run | first frame | menu interactive | loader 0→25 | bootstrap extract |
|---|---:|---:|---:|---:|
| r1 | 2744 ms | +24.64 s | 22.18 s | 1816 ms |
| r2 | 2258 ms | +24.42 s | 22.39 s | 1478 ms |
| r3 | 2230 ms | +24.86 s | 22.87 s | 1458 ms |
| **mean** | **2411 ms** | **+24.64 s** | **22.48 s** | **1584 ms** |

### 2.1 Per-phase loader durations (3-run mean, ms)

| phase | content | mean ms |
|---:|---|---:|
| 0 | `LoadChar(eckto)` | 53 |
| 1 | `text`, `skastudios`, `dbp`, `dish`, `release` | 1278 |
| 2 | `crow`/`ecktos` char defs, `dishlogo` | 156 |
| 3 | `map.ReadSegs()` + `bubbles`,`controls`,`controlsh`,`howtoplay`,`dishbuy`,`achievement` | **2393** |
| 4–5 | 19 effects (`fx/*.xnb`) | 82 |
| 6 | render targets | 102 |
| 7 | `sprites`, `jtext`, `arial` | **1634** |
| 8 | MainText + SpriteManager + level titles | 157 |
| 9 | pickups + 8 `blood*` | **1166** |
| 10 | `heart`,`pickups`,`eckto`,`items`,`comictext` | **1357** |
| 11–15 | char defs + credits | 154 |
| 16 | `gfx/maps/maps1..6` | **6200** |
| 17 | 11 heads + 15 torsos + arcade strings | **2683** |
| 18 | 12 legs + 16 weapons | **3535** |
| 19–21 | `Sound`/`MusicSound`/`VoxSound` + `intro` | 152 |
| 22 | `gfx/maps/back1..2` | **520** |
| 23–24 | `ecktog`,`ecktoa` char defs | 88 |
| 25 | `loaded = true` | 0 |
| | **sum** | **~21 708** |

**PNG accounting (single instrumented run, `split-dbg`):** 91 textures,
**20 484 ms** total, of which **decode = 18 446 ms (90 %)** and **GL upload =
2 111 ms (10 %)**. The slowest were the 1024×1024 DXT5 source atlases
(`maps1..6` ~0.8–1.2 s each, `sprites` ~1.0 s, `dbp` 1280×720 ~0.65 s).

---

## 3. Hot-spot analysis (measured, not assumed)

* **(a) PNG decode in the content manager — the whole problem.** 91 of the
  ~128 packaged PNGs are decoded at boot; they are **20.5 s of the 21.7 s
  loader (95 %)**. Within that, the CPU StbImageSharp decode is **18.4 s** and
  the UI-thread GL `SetData` is only **2.1 s**. MonoGame 3.8.5.1's
  `Texture2D.FromStream` = `StbImageSharp` decode (pure managed) + a
  `Threading.BlockOnUIThread(SetData)` upload; under Debug the managed decode
  is extremely slow.
* **(b) `.zdx`/`.dqx`/`.dgx` parsing — negligible.** `map.ReadSegs()` runs in
  phase 3 (2.39 s) but phase 3 is dominated by its 6 textures; the raw parse
  itself is a small slice of the ~150 ms of non-PNG time in that phase.
  `LoadChar` (`.dqx` char defs) phases total ~200 ms.
* **(c) Audio bank work — negligible.** `Sound.Initialize` + `MusicSound` +
  `VoxSound` = phases 19–21 = **~152 ms** (6 categories, 4 banks, 216 cues).
* **(d) Managed/JIT startup — small.** Debug first frame ~2.41 s includes the
  ~1.58 s asset **extraction** on the UI thread, leaving ~0.8 s of MonoGame/
  managed init. Release+AOT reduced that.
* **(e) Bootstrap extraction — a real, avoidable 1.5 s.** `Prepare()` copied
  all 724 assets to `files/content` **on every launch on the UI thread**. The
  PNGs are loaded straight from APK assets (`TitleContainer` →
  `AssetManager.open`); only the raw `.zdx/.dqx/.resources` need the extracted
  copy, and that copy persists in the app files dir.

---

## 4. Optimizations tried, with measured effect

### 4.1 Release build with AOT — **KEPT** (biggest single code-free win)

`dotnet build -c Release` with **no extra AOT switch** already AOT-compiles 42
assemblies (`MonoGame.Framework.dll.so`, `System.*.dll.so`, …); the Release APK
is 95 MB (arm64 only) vs 181 MB Debug. This is a configuration change, not an
algorithm change.

| | Debug | Release+AOT | delta |
|---|---:|---:|---:|
| loader 0→25 | 22.48 s | **6.18 s** | −16.3 s (3.6×) |
| decode / upload | 18.45 s / 2.11 s | **3.42 s / 1.37 s** | decode 5.4× |
| first frame | ~2.41 s | ~1.69 s (with 1.13 s extract) | −0.7 s |
| menu interactive | ~24.6 s | **22.29 s** | gate-bound (see 4.4) |

**Kept.** AOT is essential; the managed decode loop was doing most of the
damage.

### 4.2 Parallel texture decode (prefetch + thread pool) — **KEPT**

`DishwasherContentManager.Prefetch(names)` queues `Task.Run(DecodeTexture)`
for the startup texture set; `DecodeTexture` is the exact serial path
(`Texture2D.FromStream` with `ZeroTransparentPixels`, straight alpha). Decode
runs on the pool; the GL upload stays serialised on the render thread via
MonoGame's existing `BlockOnUIThread`. `GetTexture` consumes a finished prefetch
from a `ConcurrentDictionary`; **if it is not ready it decodes inline on the
caller thread** (never blocks on a worker). Called once from
`Game1.PrefetchStartupTextures()` at the end of `LoadContent()`.

| | Release serial | Release + parallel | delta |
|---|---:|---:|---:|
| loader 0→25 | 5.18–5.29 s | **1.42–1.65 s** | −3.6 s (3.4×) |
| menu interactive (6 s carousel) | 6.73 s | **6.76 s** | gate-bound |

Measured per-PNG CPU total is unchanged (~6.3 s summed across threads) but the
**wall-clock** loader time collapses because ~8 textures decode concurrently.
One texture out of 92 (`gfx/dishbuy`) is occasionally decoded twice when the
loader reaches it before its prefetch task finishes — harmless (the cache
returns one valid texture; both are disposed in `Unload`).

> **A first attempt deadlocked (reverted, then re-done correctly).** The first
> design had `Load<T>` block on the prefetch `Task`. But `LoadContent()` runs on
> the render thread, and `Threading.BlockOnUIThread` only drains via
> `Threading.Run()` inside the *next* frame; the render thread blocked on a
> worker that was blocked on the render thread → hang at
> `gfx/xbla` (last log: `Created render targets`). Fixed by never blocking on a
> worker: consume a ready result or decode inline. The inline path is safe on
> the render thread because `BlockOnUIThread` executes immediately when already
> on the thread that owns `Threading`.

### 4.3 Skip bootstrap re-extraction — **KEPT**

`AndroidContentBootstrap.Prepare()` now writes a `content.stamp` containing
`versionCode:LastUpdateTime`; if the extracted tree exists and the stamp
matches, extraction is skipped. An APK update changes the stamp and forces one
fresh extraction.

| | before | after (2nd+ launch) | delta |
|---|---:|---:|---:|
| bootstrap extract | 1.45–1.82 s | **0** (skipped) | −1.5 s |
| first frame | ~2.1 s | **~0.55–0.84 s** | −1.3–1.5 s |
| menu interactive (6 s carousel) | 8.31 s | **6.73 s** | −1.6 s |

First launch after install still extracts once (~1.5 s), so that cold run stays
~1.5 s slower.

### 4.4 Shorten the boot carousel gate — **KEPT**

The leave condition was `loaded && loadFrame > 20.45f` (`Globals.isX360` is
`true`, `SkipIntro=false`). Once the loader took 1.5 s, the remaining ~14 s of
the logo window was pure dead time. Added
`Globals.LoaderCarouselSeconds` (revertable const) and scaled the carousel
clock: `loadFrame += frameTime * (20.45f / LoaderCarouselSeconds)`. The whole
logo sequence (XBLA → Dream Build Play → ska → ESRB/dish) still plays — only
its clock is compressed — so it never cuts off mid-animation.

| `LoaderCarouselSeconds` | menu interactive (warm) |
|---|---:|
| 20.45 (original) | ~22.3 s (gate-bound) |
| 6.0 | ~6.7 s |
| **4.0 (final)** | **~5.0 s** |

4.0 s = ~1 s per logo; screenshot bursts show every logo fully drawn and
fading between, so it reads as an intentional fast publisher reel
(`load-optimization/carousel-4s.png` vs `carousel-6s.png`). Below ~4 s the
0.5 s cross-fades start overlapping and it looks like a flash — 4 s is the
sensible floor.

### 4.5 Defer non-essential assets (character/map textures) — **NOT IMPLEMENTED**

Evaluated but deliberately skipped. Rationale (measured):

* The loader is **no longer the critical path**: it finishes in ~1.5 s while the
  carousel gate is 4.0 s, so the menu appears when the *carousel* ends, not
  when the load ends. Deferring `maps/head/torso/legs/weapon` PNGs (≈13 s of
  the Debug loader, ~2.3 s of Release decode) would remove ~2.3 s of *loader*
  work but **0 s** of end-to-end time unless the carousel is cut below ~1.5 s.
* It risks exactly the inconsistent-state the brief warns about: `map.Read
  (newLevel:true)` / `ThreadedNewGameLoad` expect `mapTex`, `headTex`,
  `torsoTex`, `legsTex`, `weaponTex`, `mapbTex` populated; splitting them across
  the menu/gameplay boundary needs a new "deferred load done" gate and was not
  worth the regression risk for no end-to-end gain.

### 4.6 Other cheap wins checked

* **Duplicate/redundant loads:** none of significance. The only duplicate is
  the single prefetch race in 4.2.
* **Textures decoded but never used at startup:** all 91 are used by the menu
  or gameplay; nothing to drop.
* **Lower-cost decode path:** the PNGs are already the reduced 22 MB/128-file
  set from the content workstream. Re-encoding to DXT and shipping MonoGame
  `.xnb` *would* make load a memcpy, but it is a content-pipeline rebuild with
  premultiply/size trade-offs and was out of scope; AOT + parallel decode
  already removed the decode from the critical path.
* **`.resources`/save reads:** negligible.

---

## 5. Final results

`finalclean` = the shipped build (all `// PORT (perf)` logging removed),
Release+AOT, arm64, `LoaderCarouselSeconds=4.0`, `ParallelTextureDecode=true`,
extraction skip:

| run | first frame | menu interactive | note |
|---|---:|---:|---|
| r1 | 2342 ms | +6.51 s | first launch after install (extracts 724 files) |
| r2 | 844 ms | +5.03 s | extraction skipped |
| r3 | 831 ms | +5.01 s | extraction skipped |

* **Warm: ~5.0 s end-to-end** (was ~24.6 s measured here / ~23.3 s in the
  original note) → **~4.9× faster**, and the loader itself is ~14× faster.
* **Cold first launch after install: ~6.5 s** (one-time extraction).
* First frame: ~0.83 s warm vs ~2.41 s baseline.

---

## 6. Correctness verification (on the final APK)

* **No app errors.** `grep -iE 'FATAL|ContentLoadException|DirectoryNotFound|
  Unhandled|\[loader\] failed|\[prefetch:'` over every final launch logcat =
  empty.
* **Art / no black textures.** Title screen, main menu (Single
  Player/Multiplayer/Achievements/Leaderboards/Help & Options/Android
  Settings/Return to Game Library), Story Difficulty, Select Level, the
  per-level comic strip and the tutorial room all render with correct art, text
  and HUD (`final-menu-and-comic.png`, `gameplay.png`).
* **Menus & touch.** Touch **tap-to-select** still works — logcat:
  `[touch] tap -> select row 5 (logical 467,553 physical 700,830)` and the
  **Android Settings** screen opens from the tap
  (`android-settings-tap.png`), i.e. the Android Settings activation fix is
  intact. Keyboard A/RIGHT navigation also reaches gameplay. `MenuTouchTargets`
  / `TouchControls` / `AndroidInputBridge` were **not touched**.
* **Gameplay reachable.** Title → Main Menu → Single Player → Story Difficulty
  → Select Level → level: blackout → comic strip (restored, playing) → tutorial
  room with the character, environment, blood and HUD (`gameplay.png`).
* **Audio works.** 6 categories, 4 sound banks, 216 cues; Android log
  `com.recomp.dishwasher is now playing isMusicActive true`. No audio code
  changed.
* **Saves intact.** `profile.sav` and `settings.sav` on-device md5 are
  **byte-identical** to the pre-test backup (`00ae2cad…` and `fa33d521…`); no
  restore was needed. (The port-only `android_settings.sav` now exists with
  default values.) Save-system / autosave code untouched.
* **Protected features untouched:** `MatrixTransform`, `fade`, DXT5 alpha,
  straight-alpha/`BlendState.NonPremultiplied`, the lense sampler fix,
  landscape/immersive, widescreen (fills the panel at 1600×720 logical ×1.5),
  the restored intro, autosave, and the frame limiter/counter all keep their
  existing code. `RenderDiagnostics.Enabled=false`, `InputDiagnostics.Enabled=
  false`.
* **Manifest:** `android:targetSdkVersion="35"` preserved; the final APK is
  **not** debuggable (a temporary `android:debuggable="true"` used only to
  `run-as`-verify the saves was reverted before the final build).

---

## 7. Final configuration / how to build

```
# GameSource/projectDish/Globals.cs
public const bool  SkipIntro             = false;   // restored intro, unchanged
public const float LoaderCarouselSeconds = 4.0f;    // PORT (perf) new
public const bool  ParallelTextureDecode = true;    // PORT (perf) new
```

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android -c Release \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true \
  -p:AndroidPackageFormat=apk \
  -p:AndroidSupportedAbis=arm64-v8a
adb -s <device-serial> install -r -d \
  bin/Release/net8.0-android/com.recomp.dishwasher-Signed.apk
```

> Note: `-p:AndroidPackageFormat=apk` is required — a plain `-c Release` build
> defaults to producing an `.aab` and can leave a stale APK in `bin/Release`.
> `-p:AndroidSupportedAbis=arm64-v8a` is an optimisation for size/build time
> (the test device is arm64); drop it for a universal APK.

Artifact: `bin/Release/net8.0-android/com.recomp.dishwasher-Signed.apk`
(~95 MB, arm64, AOT).

### Revert instructions

* **Release→Debug:** build without `-c Release` (undoes 4.1; expect ~24 s).
* **Carousel duration:** `Globals.LoaderCarouselSeconds = 20.45f` restores the
  original gate exactly.
* **Parallel decode:** `Globals.ParallelTextureDecode = false` (the `Prefetch`
  call becomes a no-op; decode falls back to the serial inline path).
* **Extraction skip:** delete the `BuildStamp`/`content.stamp` block in
  `AndroidContentBootstrap.Prepare()` (or simply delete the stamp file / the
  app data) to always re-extract.

---

## 8. Files changed

| file | change |
|---|---|
| `GameSource/projectDish/Globals.cs` | `LoaderCarouselSeconds`, `ParallelTextureDecode` consts (+ comments) |
| `GameSource/projectDish/Game1.cs` | carousel-clock scale in `UpdateLoader`; `PrefetchStartupTextures()` + call at end of `LoadContent()` |
| `DishwasherContentManager.cs` | `Prefetch()` / `_prefetched` / `_prefetching`; `GetTexture` (consume-or-inline); `DecodeTexture`; thread-safe cache |
| `Platform/AndroidContentBootstrap.cs` | `content.stamp` extraction skip + `BuildStamp()` |
| `AndroidManifest.xml` | unchanged in the final state (`targetSdkVersion=35`, not debuggable) |

Temporary `// PORT (perf)` timing instrumentation (phase log, per-PNG
decode/upload log, bootstrap timer) was added for measurement and **removed
before the final build**; the only `PORT (perf)` markers left are comments on
the four retained optimisations above.

## 9. Remaining bottleneck

**The 4.0 s compressed boot-logo carousel.** With content ready in ~1.5 s, the
menu appears when the carousel gate opens (~4.0 s after the first frame) — the
loader is now fully hidden. Further end-to-end wins would require either a
shorter/optional splash (below ~4 s the cross-fades overlap and it looks like a
glitch) or moving to a MonoGame-native DXT `.xnb` content pipeline so load is a
memcpy; neither changes user-visible correctness today. Startup-to-first-frame
(~0.83 s warm, of which the app assets are already extracted) is the next
smallest target.
