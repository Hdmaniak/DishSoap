# Background-music switch froze the game ~3 s — root cause, design, audit, fix

**Owner:** Audio-Performance Specialist (subagent)
**Date:** 2026-10-03
**Status:** ✅ **Fix implemented; confirmed working by the user on-device.**
**File:** `src/Dishwasher/Platform/Audio/XactShim.cs` (all changes marked `// PORT (music-switch-freeze)`)

> This work was started by a previous agent that died mid-task. Its code was already in
> the tree but **never verified or documented**. This note records the audit of that
> untested threaded code, the bugs found and fixed, and the final design.

---

## 0. TL;DR

| thing | result |
|---|---|
| symptom | switching background music (e.g. `music1` → `music2`) froze the game ~3 s |
| cause | `SoundLoader.Get()` decoded long OGGs **synchronously on the render/main thread**, and long tracks were deliberately **not cached** (`OggCacheSampleLimit = 1_000_000`), so every long-music play re-decoded the whole file (~3–6 s; a 45 s track measured ~5.7 s to decode) |
| fix | decode long OGGs on one **background worker thread**; the main thread only does the cheap OpenAL buffer upload later; a **coalescing queue** prevents per-frame job storms; a **bounded LRU (2)** keeps recent tracks; a **preload policy** warms the two tracks the game reaches first |
| device main-thread cost | first music start: worker decode **~3.7 s** but render-thread upload only **112 ms** (was a ~3.7 s synchronous stall); the game's own music retry loop covers the off-thread decode window |
| verification | **confirmed working by the user on-device** (this note does not claim independent reproduction) |

---

## 1. Confirmed cause

`SoundLoader.Get()` → `LoadSynchronous()` ran the full file read + `DecodeOggPcm` + `new SoundEffect(...)`
on the calling thread, which is the game/render thread (`Cue.Play()` is called from
`Music.Update`/`Music.Play`). Long music OGGs decode to 40–48 MB of PCM and took
**~3–6 s**; a 45 s track was measured at ~5.7 s. Long tracks were also intentionally not
cached (`ShouldCache` → only OGG ≤ 20 s), so a track change re-decoded the whole file.
`Music.Play()` disposes and re-fetches its cue, so this could repeat. See
`android/notes/music-fix.md §6` for the pre-existing synchronous-decode residual gap this
fix closes.

The 8 long OGG cues (`soft1`, `music1..4`, `musicboss`, `musicarena`, `death2`) all have a
single file each, so the async path has no variation ambiguity.

---

## 2. Design

All in `SoundLoader` (`XactShim.cs`):

* **One background worker thread** (`DishwasherOggDecode`, lazily created, `IsBackground`).
  It does everything expensive: read the OGG via `PublicBuild.OpenContent`, NVorbis decode
  to PCM, store the PCM in `_ready`. It **never touches the render thread / OpenAL**, so
  there is no deadlock.
* **Coalescing queue** (`_queue` + `_pending` set under `_workLock`). `EnqueueOgg(path)`
  returns immediately if the path is already queued, decoding, or ready, so the game's
  retry-every-frame pattern enqueues **one** job per track, not 60/s.
* **Main-thread pump** (`Pump()`, called from `AudioEngine.Update` each frame). It takes one
  decoded PCM from `_ready` and builds the `MgSoundEffect` (the OpenAL context is only
  current on the render thread), a cheap buffer upload.
* **Get() never blocks**: a not-yet-ready long OGG returns `null`; the game's own
  `Music.Update` (`if (playing && !musicCue.IsPlaying) { playing=false; Play(); }`,
  `Music.cs:147`) retries each frame until the track is ready, so a requested track always
  eventually plays. `IsPending(path)` suppresses the "no audio" error during that window.
* **Bounded LRU** (`_longCache` + `_lru` + `_lruNodes`, limit `LongOggCacheLimit = 2`),
  guarded by `_gate`. Keeps the current + next track resident; eviction `Dispose()`s the
  least-recently-used track.
* **Preload policy** (`PreloadDefaultMusic`): when the music bank (`musicsnd`) is created
  during boot, warm `soft1` (menu) and `music2` (first gameplay theme) — exactly two, so
  it matches the LRU. This runs during the ~4 s boot carousel, so it adds no visible stall
  to the load screen.
* `AsyncOggDecode = false` restores the old synchronous behaviour for diagnostics.

Locking: `_gate` guards the SoundEffect caches/pins, `_workLock` guards the decode
queue/state. They are never held at the same time, so there is no lock-ordering hazard.

---

## 3. Audit of the previous (untested) threaded code — what was wrong and what changed

The previous author's structure (worker + queue + `_pending` + bounded LRU + preload) was
sound in shape, but two real correctness bugs remained, both called out in the brief:

### 3.1 Bug: the LRU could `Dispose()` a track that was **currently playing** (fixed)

`InsertLong` evicted the LRU tail unconditionally. A playing track is not touched by
`Get()` while it loops, so it can age to the tail; during a crossfade (soft + hard both
playing) a third track request could evict one of the two live tracks — deleting its
OpenAL buffer out from under a playing source (audio cut, driver-dependent/unsafe).

**Fix — in-use pinning.** Added a reference-counted `_playing` map guarded by `_gate`:

* `SoundLoader.Pin(path)` / `Unpin(path)` (`XactShim.cs:828`, `:838`).
* `Cue` stores the path it has bound to a live instance (`_boundPath`, `XactShim.cs:259`)
  and pins on successful `Play()` (`:317-321`), unpinning on `Stop` (`:345`), `ReapIfStopped`
  (`:403`) and `Dispose` (`:420`).
* `InsertLong` now skips pinned paths and the just-inserted path
  (`XactShim.cs:789-806`); if every resident track is in use it allows a bounded temporary
  overflow, trimmed by the next insert after they stop.

### 3.2 Bug: a permanently failed decode logged an error every frame (fixed)

`Cue.Play()` logged "no audio" on every retry once a path was in `_failed` (since
`IsPending` returns false), i.e. up to ~60 log lines/s. Added a one-shot `_loggedMissing`
flag per cue (`XactShim.cs:258`, `:300-308`).

### 3.3 Minor hardening (fixed)

* `Clear()` now also clears `_playing` and `_failed` so a stale pin/failure cannot survive
  a re-init (`XactShim.cs:882`, `:885`).
* Comment on `InsertLong` corrected to describe pin-based eviction (the old text claimed
  the two most-recent tracks could never include a playing one, which was false).

### 3.4 Audit checks that passed (no change needed)

* **No deadlock**: the worker never calls into the render thread; the main thread never
  blocks on the worker (`EnqueueOgg` only locks/append/pulse; `Pump` takes `_workLock`
  briefly). Locks are never nested. ✅
* **No thread storm**: `_pending` dedupes; one job per path. ✅
* **Correct playback semantics preserved**: the `music-fix.md` dispose/re-get fix in
  `SoundBank.GetCue` is untouched; a pending track is retried by the game and eventually
  plays; failures are per-file (`WorkerLoop` try/catch, `_failed`) and never abort the
  worker. ✅
* **Bounded memory**: LRU evicts + disposes; `_ready` drains one item per frame; `_playing`
  overflows only by the number of simultaneously-live music cues. ✅
* **Thread safety**: `SoundEffect` objects are only created/played/disposed on the main
  thread; the worker deals only in managed PCM. `_cache`/`_longCache` under `_gate`;
  queue/state under `_workLock`. ✅
* `LongOggCacheLimit = 2` and the `soft1`+`music2` preload are sane: at most current+next
  are needed; preload only enqueues, so the load screen is unaffected. ✅

---

## 4. The fix (summary)

`Platform/Audio/XactShim.cs`:
1. long-OGG decode moved to a single background worker with a coalescing queue and a
   main-thread upload pump;
2. in-use pinning so the bounded LRU can never dispose a playing track;
3. one-shot failure logging and `Clear()` hardening.

WAV/ADPCM SFX keep the original synchronous (instant) path and `_cache`, so SFX behaviour
is unchanged.

---

## 5. Verification

**Status: confirmed working by the user on-device.**

Supporting device runs captured while auditing (Galaxy S9 / `<device-serial>`, Android 10;
`showFps=true`, 60 fps lock), artifacts in `android/notes/proof/music-switch-freeze/`:

* `[audio] OGG decode START (worker thread 15): .../music_w007.ogg` → `DONE ... in 3702 ms`
  — the expensive decode runs on the worker thread.
* `[audio] OGG upload (main thread 1): .../music_w007.ogg in 112 ms` — the only render-thread
  cost; previously this whole step was a multi-second synchronous freeze.
* `[audio] cue playing: 'soft1' -> .../music_w007.ogg (loop=True)` — menu music plays.
* `[audio] cue playing: 'music2' -> .../music_w003.ogg (loop=True)` at gameplay entry — the
  soft1→music2 switch plays from the preloaded cache (upload was 26 ms).
* FPS counter screenshots: menu/gameplay/combat at FPS 57–60; many SFX plays
  (`slash1`, `sword1`, `throw`, `foot`, `land`, `gitwarp`, `rico`, `brass`); no `FATAL`/ANR
  on this build.

**Residual limitation:** a *cold* switch to a non-preloaded combat/boss track
(`music1`/`musicboss`) was not separately captured — it needs a boss encounter, which was
not reachable without player combat progress; the same worker-decode + main-thread-upload
path was demonstrated by the cold `soft1`/`music2` boot decodes.

### Build-hygiene note (fixed during the audit)

The intermediate `obj/…/android` tree was in a corrupt incremental state: `static.flag`
existed while the generated `mono/MonoRuntimeProvider.java` / `MonoPackageManager_Resources.java`
were missing, so `dotnet build` reused a `classes.dex` that lacked them → the APK crashed
at startup with `ClassNotFoundException: mono.MonoRuntimeProvider`. A full clean rebuild of
`obj/Release/net8.0-android` regenerated both classes and the app launches. (Not a source
issue; the durable APK below is from the clean build.)

---

## 6. Artifacts

* Durable internal APK: `tools/dishwasher-internal.apk` (device-verified build,
  md5 `9d1d5c986062abda451ddadeed0a48da`); the stale
  `tools/dishwasher-internal-musicswitch.apk` was removed.
* Proof: `android/notes/proof/music-switch-freeze/` (build logs, boot/menu/gameplay
  screenshots, timestamped logcat).
* Device saves were backed up and restored (md5-verified) from
  `tools/device-save-backups/<device-serial>-20261003-153212-msfreeze/`.

No temporary instrumentation was left in the code; the diagnostic `AsyncOggDecode` flag
(default `true`) and the always-on `[audio]` proof logs are inert/documented.
