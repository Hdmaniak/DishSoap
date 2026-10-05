# Background music never plays — root cause, fix, verification

**Owner:** Music-Path Investigator (subagent)
**Date:** 2026-10-03
**Status:** ✅ **Resolved.** Music now plays in-game on **both** the internal and the
public builds. The root cause was in the port's XACT shim, **not** the game logic and
**not** the assets.

> The earlier "music works" claim (a self-test that called `playCue("music3")` directly
> and saw `IsPlaying=True`) is confirmed to have been a false positive: the game's own
> music path was silently refusing every play. See §3.

---

## 0. TL;DR

| thing | result |
|---|---|
| symptom | SFX play (menu + in-game); background music never plays in either build |
| shape of bug | all music is triggered through the game class `Music`, whose `Play()` **disposes** a `Cue` and immediately re-fetches it by name |
| root cause | `Dishwasher.Audio.SoundBank.GetCue()` returned its **cached** `Cue` even after the game had disposed it; `Cue.Play()` then returned early because `IsDisposed == true` → a silent no-op repeated every frame |
| fix | `SoundBank.GetCue` only returns a cache hit while it is **not** disposed; otherwise it creates a fresh `Cue` (XNA semantics). 1 method, ~6 lines, in `Platform/Audio/XactShim.cs` |
| why SFX were fine | SFX go through `SoundBank.PlayCue` (fire-and-forget) and never dispose a cue, so the cached object stayed alive |
| device proof | pre-fix: `Cue.Play('soft1') -> REFUSED: IsDisposed=true` ~60×/s forever. post-fix internal: `soft1` (menu) and `music2` (gameplay) both play (`... state=Playing`, Music volume ramps to 1.0); public: derived OGG plays; 0 `REFUSED`, no crash/ANR |

---

## 1. How the game triggers music (trigger map)

Source of truth: `managed/decompiled/game/projectDish/` (game logic) and
`src/Dishwasher/GameSource/projectDish/` (the compiled port — identical logic,
`using Dishwasher.Audio` instead of `Microsoft.Xna.Framework.Audio`). Line numbers
given for the **port** (decompiled equivalents in parentheses).

### 1.1 Engine / banks (once, in the loader)

* `Sound.Initialize()` — `Sound.cs:23-30`
  `new AudioEngine("sfx/sfxproj.xgs")`, `new WaveBank(engine,"sfx/waves.xwb")`,
  `new SoundBank(engine,"sfx/sounds.xsb")`, category `Default`.
* `MusicSound.Initialize()` — `MusicSound.cs:21-27` (decompiled `MusicSound.cs:21-27`)
  ```csharp
  engine = Sound.getAudioEngine();
  wave  = new WaveBank(engine, "sfx/music.xwb", 0, 16);   // STREAMING ctor
  sound = new SoundBank(engine, "sfx/musicsnd.xsb");
  ```
  Called from the loader at `Game1.cs:708` (decompiled `Game1.cs:664`, loader case 20).
* Cue names in the music bank: `music1–music4`, `musicboss`, `musicarena`, `soft1`,
  `death2` (8 tracks). **Category = `Music` (XGS id 2)** — `category_id: 2` for all of
  them in `Assets/sfx/cue_map.json`; the game also gets the bus explicitly with
  `Sound.getAudioEngine().GetCategory("Music")` (`Music.cs:28`, `Intro.cs:84`).
* All music is OGG (`ogg/music/music_wNNN.ogg`); every other cue is WAV/ADPCM.

### 1.2 The background-music objects

* Created once, entering the menu — `Game1.cs:2505-2506` (decompiled `2393-2394`):
  ```csharp
  hardMusic = new Music("music2", newSoft: false);
  softMusic = new Music("soft1", newSoft: true);
  ```
* Updated every frame while `gameMode ∉ {3,4,5,6}` — `Game1.cs:1379-1380`
  (decompiled `1285-1286`):
  ```csharp
  hardMusic.Update(character, softMusic, map);
  softMusic.Update(character, hardMusic, map);
  ```
* `Music.Update` picks the track from the monster state and cross-fades the `Music`
  bus: `music1`/`music2`/`musicboss`/`musicarena` for `hardMusic`, `soft1` for
  `softMusic`; `music.SetVolume(vol * Globals.settings.bgmVol/10)` (`Music.cs:78,110,144`).

### 1.3 The play/stop path (the bug)

`Music.cs:38-57` (decompiled `Music.cs:38-57`), and `setMusic` at `Music.cs:31-36`:

```csharp
public void Play()
{
    if (!playing)
    {
        if (musicCue.IsPaused) musicCue.Resume();
        else if (!musicCue.IsPlaying && !musicCue.IsPreparing)
        {
            if (!musicCue.IsDisposed) musicCue.Dispose();   // <-- disposes the cue …
            musicCue = MusicSound.getCue(musicString);      // <-- … then re-fetches by name
            musicCue.Play();
        }
    }
    playing = true;
}
```

`MusicSound.getCue` → `SoundBank.GetCue` (`MusicSound.cs:50-53` / `Sound.cs:58-61`).
This "dispose then GetCue again to restart" is legitimate XNA usage: **XNA's
`SoundBank.GetCue` returns a fresh, playable `Cue`**, so disposing the old handle is
harmless. The shim broke that contract (below).

### 1.4 Other (direct) music triggers

* `Intro.cs:126-127` `MusicSound.getCue("music3"); myMusic.Play();` — but the Intro
  sequence (`gameMode == 3`) is **unreachable**: `Game1.cs` sets `gameMode = 3;`
  immediately followed by `gameMode = 1;` in the same call (port `2510/2512`,
  decompiled `2395/2396`). So `music3`/the logo-sequence music is dead code in this
  build (pre-existing game logic, unchanged).
* `Game1.cs:1359` `death2` (death sting) — direct `getCue` + `Play()` (no dispose),
  so it already worked pre-fix.
* `Credits.cs:284,298` `music4` — direct, no dispose.
* `GuitarSolo.cs` `fgCue`/`bgCue`, `GuitarHalper.cs:407` — solo-mode cues.

The **only** path used for menu/in-game background music is the `Music` class (§1.2/1.3).

---

## 2. Root cause

`Platform/Audio/XactShim.cs`, `SoundBank.GetCue` (pre-fix):

```csharp
if (_cache.TryGetValue(name, out var c)) return c;   // returns the dead object
```

The shim caches `Cue` objects by name. `Cue.Dispose()` sets `IsDisposed = true`, and
`Cue.Play()` begins with `if (IsDisposed || _def == null || _def.Files.Length == 0) return;`.
Therefore:

1. `Music.Play()` calls `musicCue.Dispose()` → the cached `Cue` for that name is marked disposed.
2. `MusicSound.getCue(name)` returns **the same cached object** (now disposed).
3. `musicCue.Play()` returns immediately → **silence**.
4. `Music.Update` sees `playing && !musicCue.IsPlaying` and calls `Play()` again next
   frame → the same silent dispose/re-get loop, forever, at ~60 Hz.

SFX were unaffected because `Sound.playCue(name)` → `SoundBank.PlayCue` never disposes
a cue, so the cached object stayed alive and playable.

This is why the symptom was complete and identical on both builds: the OGG decoder and
the cue map were fine; the game simply was never able to hand a live cue to playback.

---

## 3. Instrumented runtime evidence (game-driven)

Temporary `// PORT (musicdiag)` instrumentation logged every `GetCue` / `Play` /
`Dispose` (name + category + outcome) and every `Music`-bus `SetVolume`. It has been
**removed** after the work; the fix itself is permanent. Raw logs:
`android/notes/proof/music-fix/02-prefix-internal.log` (pre-fix) and
`03-*`, `13-*`, `28-*` (post-fix).

### 3.1 Pre-fix — the game *does* request music, and the shim refuses it

Internal build, main menu (no input, no self-test):

```
[musicdiag] GetCue('music2') cat=2('Music') -> fresh cue, files=1
[musicdiag] GetCue('soft1')  cat=2('Music') -> fresh cue, files=1
[musicdiag] Cue.Dispose('soft1')
[musicdiag] GetCue('soft1') cat=2 -> cache-hit, disposed=True (returning cached object)
[musicdiag] Cue.Play('soft1') -> REFUSED: IsDisposed=true (stale cached cue)
[musicdiag] GetCue('soft1') cat=2 -> cache-hit, disposed=True (returning cached object)
[musicdiag] Cue.Play('soft1') -> REFUSED: IsDisposed=true (stale cached cue)
... (repeated ~60×/s, forever)
```

→ **The game requests the Music cue; the shim fails to play it.** (Not an upstream
"never asks" problem, and not an OGG/decoder problem.)

### 3.2 Post-fix — internal, menu and gameplay

Menu (`03-fixed-internal-menu.log`):
```
[musicdiag] Cue.Dispose('soft1')
[musicdiag] GetCue('soft1') cat=2 -> cache-hit(DISPOSED/STALE) -> discarding, creating fresh cue
[musicdiag] GetCue('soft1') cat=2('Music') -> fresh cue, files=1
[audio] OGG decoded: sfx/ogg/music/music_w007.ogg (237.2s, 00:03:57.1893310)
[musicdiag] Cue.Play('soft1') cat=Music file=... music_w007.ogg loop=True vol=0.00 -> state=Playing
... Category.SetVolume('Music', …) ramps 0.000 → 1.000
REFUSED count = 0
```

Gameplay, FOGHORN CAFE (`13-fixed-ingame-full.log`): the game cross-fades `soft1` out
and starts the gameplay track —
```
[musicdiag] Cue.Dispose('music2')
[musicdiag] GetCue('music2') cat=2 -> cache-hit(DISPOSED/STALE) -> discarding, creating fresh cue
[musicdiag] Cue.Play('music2') cat=Music file=sfx/ogg/music/music_w003.ogg loop=True vol=0.00 -> state=Playing
[audio] cue playing: 'music2' -> ... music_w003.ogg (loop=True)
... Category.SetVolume('Music', …) ramps to 1.000
REFUSED count = 0 ; SFX plays = 122 ; no FATAL/ANR
```

### 3.3 Post-fix — public build (on-device derivation)

Public build (`-p:DishwasherPublic=true`), retail XBLA zip imported on the S9:

```
[import] audio: 222/222 waves derived (208 wav, 14 ogg), 216 cues, native=True
[import] SUCCESS All 458 required original files verified (size + SHA-256). | Audio: 222/222 waves, 216 cues
[audio] SoundBank ready: 'sfx/musicsnd.xsb' -> 216 cues registered
[audio] OGG decoded: sfx/ogg/music/music_w007.ogg (237.2s, 00:03:57.1893310)
[musicdiag] Cue.Play('soft1') cat=Music file=... music_w007.ogg loop=True vol=0.00 -> state=Playing
REFUSED count = 0
```

→ the public build plays a Music-category cue from a **derived** OGG with the fixed shim.

### 3.4 Final builds (instrumentation removed)

Both final APKs still log the (pre-existing, always-on) `[audio] cue playing: …` proof:
* internal: `[audio] cue playing: 'soft1' -> sfx/ogg/music/music_w007.ogg (loop=True)`
* public:   `[audio] cue playing: 'soft1' -> sfx/ogg/music/music_w007.ogg (loop=True)`

No `FATAL EXCEPTION` / `ANR in com.recomp.dishwasher` in any run.

---

## 4. The fix

`src/Dishwasher/Platform/Audio/XactShim.cs` — `SoundBank.GetCue` only:

```csharp
public Cue GetCue(string name)
{
    if (string.IsNullOrEmpty(name)) return Cue.Silent("");
    if (_cache.TryGetValue(name, out var c))
    {
        // PORT FIX (music): a cache hit that has ALREADY been disposed must not be
        // handed back: the game's Music.Play()/Music.setMusic() dispose a Cue and
        // then immediately re-fetch it by name to (re)start it. XNA's
        // SoundBank.GetCue returns a fresh, playable Cue for that. The old shim
        // returned the same dead cached object, so every music Play was a silent
        // no-op (SFX were unaffected because they never dispose).
        if (!c.IsDisposed) return c;
    }

    if (CueDatabase.TryGet(name, out var def))
    {
        var cat = _engine.GetCategoryById(def.CategoryId);
        c = new Cue(name, cat, def);
    }
    else
    {
        CueDatabase.WarnOnce(name);
        c = Cue.Silent(name);
    }
    _cache[name] = c;
    return c;
}
```

Properties of the fix:
* **Root cause, not a workaround** — it restores XNA's contract that `GetCue` yields a
  playable cue; it does not special-case cue names or the `Music` class.
* **No SFX regression** — SFX never hit the disposed branch, so their behaviour is
  byte-for-byte unchanged (verified: 122 SFX plays in-game, no errors).
* **No public-build special-casing** — the same shim file/`GetCue` path serves both
  builds; derived OGGs load exactly like bundled ones.
* **No game-logic edits** — `GameSource/` and `managed/decompiled/` untouched.
* Both `Music.Play()` and `Music.setMusic()` (both dispose-then-re-get) are fixed by the
  one change. `SoundBank._cache` still holds at most one live cue per name, so there is
  no leak: the disposed cue is dropped and replaced.

---

## 5. Verification summary

| build | scene | evidence | result |
|---|---|---|---|
| internal (pre-fix) | main menu | `Cue.Play('soft1') -> REFUSED: IsDisposed=true` (~60/s, forever) | music silent (bug reproduced) |
| internal (fixed) | main menu | `soft1 → state=Playing`, Music vol 0→1.0, 0 REFUSED | ✅ music plays |
| internal (fixed) | FOGHORN CAFE gameplay | `music2 → state=Playing`, Music vol →1.0, 122 SFX plays, no crash/ANR | ✅ in-game music plays |
| public (fixed, instrumented) | import + menu | 222/222 waves (208 WAV, 14 OGG), 216 cues; `soft1 → state=Playing` from derived OGG, 0 REFUSED | ✅ derived music plays |
| internal (final) | menu | `[audio] cue playing: 'soft1'` | ✅ |
| public (final) | menu | `[audio] cue playing: 'soft1'` | ✅ |

Device: SM-G960F (Galaxy S9, Android 10), serial `<device-serial>`.
Saves backed up before testing and verified unchanged afterwards (md5 identical:
`profile.sav`, `settings.sav`, `android_settings.sav`). OneDrive document provider was
disabled only to stabilise the SAF picker and was re-enabled; the pushed retail zip was
deleted from the device.

Proof artifacts: `android/notes/proof/music-fix/` (`02-prefix-internal.log`,
`03-fixed-internal-menu.log`, `13-fixed-ingame-full.log`, `28-public-menu.log`,
`38/40-public-*.log`, `50-final-internal-menu.log`, `52-final-public-menu.log`, plus
screenshots).

---

## 6. Residual gaps / notes

* **Enemy-encounter `music2` on the public build** was not separately captured: reaching
  the encounter requires physical movement, and no controller was available in the
  automated public run. The public build *did* play a Music-category cue from a derived
  OGG through the exact same fixed shim path, and the internal run proved the
  `music2` switch. This is a testing gap, not a functional one.
* **First-play OGG decode stall.** Long music OGGs are decoded synchronously on first
  play (and are intentionally not cached, `SoundLoader.ShouldCache` → `>20 s`), so the
  first `soft1`/`music2` start hitches the game thread for ~3–5 s (soft1 237 s track
  measured at ~5.3 s on the S9). This is pre-existing shim behaviour that simply was
  never observable while music never played. Not required for this fix; a good follow-up
  is to decode the next music track on a background thread (or cache the decoded PCM).
  It did not produce an ANR (decode runs on the game thread, not the Android UI thread).
* **`music3` / Intro sequence** is unreachable because `Game1.cs` sets `gameMode = 3;`
  then immediately `gameMode = 1;` (decompiled `2395/2396`, port `2510/2512`) — the
  logo-sequence `Intro` (and its `music3`) never runs. This is original game logic, not
  a port regression, and is independent of the music fix.
