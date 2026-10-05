# Public (content-free) build + first-run import / on-device derivation

**Owner:** Distribution/Compliance Architect (subagent)
**Date:** 2026-10-03
**Status:** ✅ Implemented and verified on a Galaxy S9 (Android 10). Stage 2 (audio) is also complete — see `public-build-audio.md`.

This document covers the **second, "public" build variant** of the port: an APK that
contains **no game content**, asks the owner to supply their own copy on first run,
**checks it against the SHA-256 manifest**, and **derives the textures and fonts
on-device**. The existing build is unchanged and stays the "internal" build.

> **Update 2026-10-05 — tolerant import (policy change).** The check no longer
> blocks on an asset that differs (a different game revision) or is missing: both
> are **warnings** and the import continues with whatever is present. The import
> is only refused when there is **nothing usable** (no usable file, or a missing
> *core boot file*). See §3.5. Ownership gating can be re-enabled at build time
> with `-p:DishwasherStrictContent=true`. The sections below marked "rejected"
> describe the original (strict) behaviour and are kept for history.

---

## 0. TL;DR

| thing | result |
|---|---|
| build switch | `-p:DishwasherPublic=true` (defines `DISHWASHER_PUBLIC`) |
| public APK assets | **43 files**: `assets/fx/` (21) + `assets/Content/fx/` (21) + `import-manifest.json` |
| internal APK assets | **712 files**: `data/` 303, `sfx/` 223, `gfx/` 132, `fx/` 21, `Content/` 21, `Resources/` 11, manifest |
| public APK size | 27.2 MB (internal 96.1 MB) |
| manifest | 458 original files (data 303 + gfx xnb 128 + gfx maps.zdx 1 + Resources 11 + sfx banks 15) |
| validation | **tolerant** size + SHA-256 per file (2026-10-05): mismatched/missing → warning + continue; only "nothing usable" blocks |
| derivation | XNB v2 → straight-alpha PNG (125 textures) + 3 SpriteFont atlases + `.spritefont.json` |
| proof of correctness | C# derived output is **pixel-identical** to the Python reference exporter (128/128 PNGs, 3/3 JSONs) |
| audio (Stage 2, done) | 360 XMA decoded on-device: **222/222 waves (208 WAV + 14 OGG), 216 cues**; see `public-build-audio.md` |
| device test | import success (`458/458`), rejection (`443 missing`), game boots with derived textures + audio (216 cues) |

---

## 1. Build switch

The **same source tree** produces both variants. Add `-p:DishwasherPublic=true` for
the public build; omit it (or pass `false`) for the internal build.

```sh
source tools/scripts/env.sh
cd src/Dishwasher

# internal (unchanged behaviour, bundles all assets)
dotnet build -c Release -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormat=apk

# public (content-free; ships only our own work)
dotnet build -c Release -f net8.0-android -p:DishwasherPublic=true \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormat=apk
```

`Dishwasher.csproj` changes:

```xml
<PropertyGroup Condition="'$(DishwasherPublic)' == 'true'">
  <DefineConstants>$(DefineConstants);DISHWASHER_PUBLIC</DefineConstants>
</PropertyGroup>
<ItemGroup Condition="'$(DishwasherPublic)' == 'true'">
  <AndroidAsset Remove="Assets\data\**" />
  <AndroidAsset Remove="Assets\gfx\**" />
  <AndroidAsset Remove="Assets\sfx\**" />
  <AndroidAsset Remove="Assets\Resources\**" />
</ItemGroup>
```

`PublicBuild.IsPublic` is a compile-time `const`, so the internal build pays nothing
and the import screen is not reachable there.

### 1.1 The stale-asset trap (fixed)

The Android SDK stages `@(AndroidAsset)` into `obj/.../assets` and only updates it
incrementally (`_GenerateAndroidAssetsDir`, with `RemoveUnknownFiles`). Switching
**internal → public** leaves the previously copied game assets behind because the
public item set is a strict subset and the target is considered up to date — the
first public build still contained `data/gfx/sfx/Resources`. Fixed with a
public-only target that deletes the staging dir first:

```xml
<Target Name="DishwasherPublicClearStagedAssets"
        Condition="'$(DishwasherPublic)' == 'true'"
        BeforeTargets="_GenerateAndroidAssetsDir">
  <RemoveDir Directories="$(MonoAndroidAssetsDirIntermediate)"
             Condition="Exists('$(MonoAndroidAssetsDirIntermediate)')" />
</Target>
```

Verified in both directions (public→internal restores all assets; internal→public
drops them). See `proof/public-build/asset-listings.txt`.

---

## 2. What is excluded and why

Measured from `src/Dishwasher/Assets/`:

| tree | size / files | kind | public build |
|---|---|---|---|
| `data/` | 7.3 MB / 303 | **ORIGINAL** `.zdx`/`.dqx`/`.dgx` from the package | excluded — user supplies |
| `Resources/` | 656 KB / 11 | **ORIGINAL** `.resources` strings | excluded — user supplies |
| `gfx/` | 19 MB / 132 | **DERIVED** 125 PNGs + 3 font atlases + `.spritefont.json` | excluded — must never ship |
| `sfx/` | 48 MB / 223 | **DERIVED** 222 OGG/WAV + `cue_map.json` | excluded — must never ship (Stage 2) |
| `fx/` | 180 KB / 21 | **OURS** — 19 effects re-authored from Xenos microcode + 2 sketches, compiled by MGCB | **kept** |

`Assets/fx` and the compiled `Content/fx`→`assets/Content/fx` are our own artifacts
(re-authored HLSL → MonoGame `.xnb`), not Xbox content, so they ship. The
`fx/` exclusion rationale is documented so a reviewer can re-judge it.

The public build additionally ships **`Assets/import-manifest.json`** (63 KB), a
read-only proof-of-ownership manifest (see §4). The public APK asset tree is exactly:

```
assets/Content/fx/*.xnb   (21)   assets/fx/*.xnb (21)   assets/import-manifest.json
```

Proof: `android/notes/proof/public-build/asset-listings.txt`.

---

## 3. First-run import + validation UX

### 3.1 Gate

`Activity1.OnCreate` runs `AndroidContentBootstrap.Prepare()` as before. In the
public build it then checks `PublicBuild.ContentReady()`: a marker
`files/content/.import-complete` plus a handful of key derived files. If not ready it
shows the **import screen** instead of booting; the internal build always proceeds.

### 3.2 Import screen (native Android widgets, touch-first)

`Platform/Import/ImportController.cs` builds a scrollable `LinearLayout`:

* explanation + the honest audio note;
* **SELECT FOLDER** → `ACTION_OPEN_DOCUMENT_TREE`;
* **SELECT ZIP / PACKAGE** → `ACTION_OPEN_DOCUMENT` (`*/*`, opens at `Download` when
  the provider supports `EXTRA_INITIAL_URI`);
* a horizontal progress bar + status line (`Verifying 137/443 head1.xnb`);
* a scrollable, colour-coded report listing every missing / mismatched file;
* **START GAME**, enabled only after a successful validate + derive.

All work runs on a background `Thread`; progress is marshalled to the UI with
`RunOnUiThread`. No ANR: the UI thread only paints. The report is reachable by
scrolling (verified — the first revision cut it off at 720p landscape; fixed).

### 3.3 Sources accepted

`ContentSourceFactory.FromLocalFile` sniffs the bytes (SAF streams are copied to app
cache first):

1. **folder** of extracted files → `SafTreeContentSource` walks the tree with
   `DocumentsContract` and opens each document by URI;
2. a **zip of extracted files** → `ZipContentSource`;
3. **the retail XBLA `.zip`** whose single entry is a LIVE/CON/PIRS package →
   unpacked to cache and read by `StfsContentSource`;
4. a raw **STFS/LIVE package** file.

Backslash paths are normalised to `/`. All three shapes were validated against the
genuine package (STFS, extracted-zip, folder: 443/443 each).

### 3.4 Starting the game after import

The game cannot simply be `Run()` from the import screen: the Activity's `OnResume`
already fired while no `Game` existed, so MonoGame's activity-resumed callback that
starts the GL loop never runs (observed: black screen, no `Initialize` traces). After
a successful import, **START GAME calls `Activity.Recreate()`** so the game boots
through the normal `OnCreate → OnResume` lifecycle. Verified on device.

### 3.5 Tolerant validation (2026-10-05) — the current shipped behaviour

`ContentImporter.Validate` still hashes every manifest entry, but the result is used
tolerantly (`ImportReport`):

| outcome | meaning | action |
|---|---|---|
| **matched** | present, byte-identical to the reference | used as-is |
| **differed** | present, different size or SHA-256 (a different revision) | **warn + import as-is** |
| **missing** | absent from the supplied selection | **warn + skip** (that asset is not derived) |
| **nothing usable** | no usable file, **or** any core boot file absent | **block** |

**"Nothing usable" rule.** The import is refused only when `Usable == 0`, or when
any of the four **core boot files** — the ones the game needs to reach its menu —
is absent (matched *or* differed counts as present):

```
data/levels.zdx
gfx/text.xnb
gfx/Arials.xnb
Resources/dishX.Resources.Strings.resources
```

Everything else (audio banks, extra textures, localizations, ...) is optional: a
missing `sfx/*.xwb`/`.xsb` simply means that bank is absent (`AudioImporter` derives
what it can and always writes `sfx/cue_map.json`, possibly with 0 cues), and a
missing texture just means that art will not be derived. `PublicBuild.ContentReady`
was updated to drop the `sfx/cue_map.json` sanity key so a silent-but-playable
import still counts as ready.

**Flag.** `PublicBuild.StrictContentValidation` defaults **false**. Set it at runtime,
or build with `-p:DishwasherStrictContent=true` (which defines `DISHWASHER_STRICT_CONTENT`)
to restore the old gate where any missing/mismatched file blocks with a per-file report.

**Reporting.** The import screen shows a concise summary
(`X matched · Y differed · Z missing`) plus one explicit line saying whether the
import is *fine, continuing* or which features may be missing (with a specific note
when audio banks are absent). The **full 458-line per-file detail goes to the log**
(`logcat -s Dishwasher`, `[import]` lines), not the screen.

**Robustness.** `StageAndDerive` skips absent files and wraps each `DeriveGfx`/copy in
its own try/catch, so a single bad/mismatched asset is logged and skipped and can never
abort the whole import. `AudioImporter` was already per-wave tolerant.

---

## 4. Checksum manifest

`src/Dishwasher/Assets/import-manifest.json` (packaged read-only; extracted by
the bootstrap, never user-editable):

```json
{
  "version": 1,
  "algorithm": "sha256",
  "required_counts": {"data":303,"gfx_xnb":128,"gfx_zdx":1,"resources":11},
  "required_total": 443,
  "files": [ {"path":"data/levels.zdx","size":331,"sha256":"63be13…"}, … ]
}
```

Required set = only what the public build consumes:

* `data/**` (303) — original scripts / character data / solo data;
* `gfx/**/*.xnb` (128) — original XNB v2 textures + SpriteFonts (derived on device);
* `gfx/maps/maps.zdx` (1) — original map-segment script read with `File.Open`;
* `Resources/**` (11) — original `.resources` strings.

**Nothing derived is ever listed** (no PNG, OGG, WAV, `cue_map.json`,
`.spritefont.json`, or our compiled fx). Hashes are **SHA-256 of the raw file bytes**
plus the exact byte size; a file must match both.

Regenerate (from our genuine cleaned extraction, byte-identical to the STFS package):

```sh
python3 tools/gen_import_manifest.py \
  --source $DISHWASHER_ASSETS_DIR \
  --out    src/Dishwasher/Assets/import-manifest.json
# required total: 443  counts: {'data':303,'gfx_xnb':128,'gfx_zdx':1,'resources':11}
```

Validation result handling (current tolerant policy — see §3.5):

* a mismatched file → **warning, imported as-is**;
* a missing file → **warning, skipped**;
* nothing usable (no usable file, or a missing core boot file) → **rejected** with a clear message;
* otherwise → stage + derive whatever is present, then write `.import-complete` and enable START.

The original strict behaviour (kept for reference / re-enabled with
`-p:DishwasherStrictContent=true`) was: any missing or mismatched file → rejected,
nothing staged, per-file report shown.

### 4.1 STFS/LIVE reader

Ported from the validated Python reference `tools/stfs_extract.py` (itself
derived from Xenia/ReXGlue's `stfs_container_device.cpp`). It handles the live
package quirks measured here: `header_size` at 0x340, the volume descriptor at
0x379, a **little-endian** `file_table_block_count` (a plain `uint16_t` in the C++
struct), the 170-blocks-per-level-0-hash-table layout, and the level-0 hash table
chain for file blocks. It reproduces `assets-clean` **byte-for-byte** (560/560 files,
with one stray `sfx/solo_crux.xwb.wav` that only exists in `assets-clean`).

---

## 5. On-device derivation

From the verified originals, `ContentImporter.StageAndDerive` writes into app-private
`files/content`:

| input | on-device output |
|---|---|
| `gfx/**.xnb` `Texture2DReader` | `gfx/**.png` (straight-alpha, same as the Python exporter) |
| `gfx/**.xnb` `SpriteFontReader` | `gfx/**_atlas.png` + `gfx/**.spritefont.json` |
| everything else (`data/**`, `Resources/**`, `gfx/maps/maps.zdx`) | copied verbatim |

`DishwasherContentManager`/the game are **unchanged at the call sites**: the derived
files use exactly the layout the existing loader already expects (`asset + ".png"`,
`asset + "_atlas.png"`, `asset + ".spritefont.json"`).

Two supporting pieces:

* **`Platform/Import/XnbV2.cs`** — faithful C# port of `tools/xnb_v2.py`:
  XNB v2 header (magic + platform + Int16 version + Int32 size, no flags byte),
  reader table, `Texture2D` formats 1 (raw A,R,G,B) / 28 (DXT1) / 32 (DXT5) with
  **big-endian RGB565 endpoints** and the **word-swapped DXT5 alpha block**, and the
  GS3.0 `SpriteFontReader` (`List<char>` UTF-8, no `defaultCharacter`).
* **`Platform/Import/PngWriter.cs`** — dependency-free RGBA8888 → PNG encoder
  (filter 0, colour type 6, zlib via `DeflateStream` + Adler32 + CRC32).

`PublicBuild.OpenContent` resolves content **app-private files first, then APK
assets**, which is what lets the derived PNGs be seen (stock `TitleContainer` on
Android only reads APK assets).

**Correctness proof (desktop harness):** `tools/csharp/importer-test/`
links the cross-platform importer sources, validates the real STFS package, stages +
derives all 443, and the result was compared with Pillow against the Python
reference `src/Dishwasher/Content/gfx/`: **128/128 PNGs pixel-identical, 0
dimension mismatches, 3/3 `.spritefont.json` field-identical.** On device the same
code produced a bootable `files/content/gfx` tree.

---

## 6. Audio story (Stage 2 complete)

On-device XMA decoding is implemented and verified — see
**`public-build-audio.md`** for the decoder strategy/licensing, the C# container
parsing and cue mapping, the import wiring, the manifest change and the device
evidence. In short: the public build now decodes the owner's `sfx/*.xwb` banks
into **222/222 waves (208 PCM16 WAV + 14 OGG) and 216 cues**, matching the
internal build, and the game boots with SFX working.

The earlier Stage-1 behaviour (0 cues registered, silent, no crash) is superseded.
`SoundBank.GetCue` still returns a silent placeholder for unknown cues and
`SoundLoader.Get` still returns `null` on a missing file, so a missing/failed
derivation can never crash the game.

> Background **music** does not play in the internal build either — that is a
> separate pre-existing issue, out of scope for this work.

---

## 7. Verification evidence

All under `android/notes/proof/public-build/`:

| file | shows |
|---|---|
| `01-import-screen.png` | first-run import screen (touch UI, audio note) |
| `09-picker-open.png`, `27-picker-grid2.png` | SAF document picker |
| `13-after-providers.png`, `20-search-result.png` | progress state (`Importing 428/443 …`) — no ANR |
| `15-import-success-details.png` | `✓ Import complete: All 443 required original files verified (size + SHA-256)` + START |
| `30-failure-details.png` | rejection report: file list, `… and 418 more`, `Mismatched: (none)` |
| `25-recreate-boot.png` | game booted after import→START with derived textures |
| `31-internal-restored-boot.png` | internal build restored, full content (216 cues) |
| `logcat-import-failure.txt`, `logcat-full-session.txt` | validation + boot + audio logcat |
| `asset-listings.txt` | public vs internal `assets/` listing |

Key logcat:

```
[import] public import screen shown
[import] SUCCESS All 443 required original files verified (size + SHA-256).
[import] content ready; restarting into game
[trace] Initialize called … [trace] [boot] content loaded; entering main menu
[audio] SoundBank ready: 'sfx/sounds.xsb' -> 0 cues registered      # public: silent
```

Rejection (`logcat-import-failure.txt`):

```
[import] FAILED VERIFICATION FAILED: 443 missing, 0 mismatched, 0/443 ok.
```

Desktop matrix (all 443 verified + derived):

| source | validate | derive |
|---|---|---|
| STFS/LIVE package | 443/443 | 128 PNG + 3 JSON + 315 staged |
| ROM `.zip` (STFS inside) | 443/443 | yes |
| zip of extracted files | 443/443 | yes |
| folder of extracted files | 443/443 | yes |
| corrupt/incomplete | 442 missing, 1 mismatched → rejected | — |

Device notes: tested on `<device-serial>` (Galaxy S9, Android 10). Saves were backed
up first (`tools/device-save-backups/<device-serial>-20261003-135430/`, md5
verified) and **restored byte-identically**; the internal APK was reinstalled and
boots with the restored saves. Two Samsung cloud DocumentsProviders
(`com.microsoft.skydrive`, `com.microsoft.appmanager`) crashed the system picker
during automation; they were disabled for the test and **re-enabled afterwards**.
The staged test zips were removed from `/sdcard/Download`.

---

## 8. Remaining work (Stage 2 done)

Stage 2 (audio) is complete — see `public-build-audio.md`. Items 1–3 below are
implemented; item 4 (atomic temp-dir derivation, free-space check, replace-content
button) remains optional polish.

1. ✅ **XMA → PCM/OGG decoder**: `Platform/Import/{XwbReader,XsbReader,AudioImporter}.cs`
   + `Platform/Audio/NativeXma.cs` + `libdishaudio.so`; 222/222 waves, 216 cues.
2. ✅ `sfx/**` originals added to the manifest (443 → 458); no derived audio listed.
3. ✅ import screen note + success counts updated.
4. ⬜ optional polish: derive into a temp dir and atomically rename; verify each
   derived PNG's dimensions before writing the completion marker; expose a
   "replace content" button; show a free-space check before import.

Non-goals (still true): no gameplay changes, no content is ever shipped in the
public APK, and audio never crashes.

---

## 9. Files touched

**Added** (under `src/Dishwasher/`, all marked `// PORT`):

* `Platform/PublicBuild.cs` — switch, readiness, content-open fallback, marker.
* `Platform/Import/XnbV2.cs`, `PngWriter.cs`, `StfsReader.cs`,
  `GameContentSource.cs`, `ContentSourceFactory.cs`, `AndroidContentSource.cs`,
  `ImportManifest.cs`, `ContentImporter.cs`, `ImportController.cs`.
* `Assets/import-manifest.json` (generated, read-only).

**Added tooling** (`tools/`):

* `stfs_extract.py` — validated STFS/LIVE reference extractor.
* `gen_import_manifest.py` — manifest generator.
* `csharp/importer-test/` — desktop harness linking the importer sources.
* `xma-decoder/` — Stage 2: native XMA decoder source/build script + licences.

**Stage 2 (audio) additions/changes** are listed in full in
`public-build-audio.md` §8: `Platform/Import/{XwbReader,XsbReader,AudioImporter}.cs`,
`Platform/Audio/NativeXma.cs`, `Platform/Audio/native/arm64-v8a/*.so`, the
`ContentImporter`/`ImportController`/`PublicBuild`/`XactShim`/`Dishwasher.csproj`
edits, and the regenerated 458-entry `import-manifest.json`.

**Modified:**

* `Dishwasher.csproj` — public switch, asset exclusions, staging-clear target.
* `Activity1.cs` — public gate, `StartGame()`, `RestartForGame()` (`Recreate`),
  `OnActivityResult`, import-aware `OnBackPressed`.
* `DishwasherContentManager.cs` — `OpenContent` now goes through
  `PublicBuild.OpenContent` (files-first, then APK) instead of `TitleContainer`.

**Not touched:** `GameSource/**` (game logic), the 19 `Content/fx` re-authored
effects, and every protected fix (touch/tap-select, `MatrixTransform`, fade, DXT5
alpha, straight-alpha/`NonPremultiplied`, lense sampler, landscape/immersive/
rotation, widescreen, intro, autosave, settings, FPS counter/limiter, LAN). Both
diagnostic harnesses remain `Enabled=false`.
