# Wine-backed MGCB Android/OpenGL shader build

**Author:** Build-Infrastructure Specialist (subagent)
**Date:** 2026-10-02
**Host:** Ubuntu 26.04 (Resolute) x86_64, .NET SDK 8.0.425, MGCB v3.8.5.1
**Status:** ✅ **WORKING** — `/platform:Android` and `/platform:DesktopGL` compile the 6
re-authored shaders to MGFX through Wine. `/platform:XboxOne` still compiles natively.

---

## 0. TL;DR

| item | value |
|---|---|
| Wine | `wine-10.0 (Ubuntu 10.0~repack-12ubuntu1)`, apt `wine` + `wine64` (universe) |
| Prefix | `$HOME/.wine-mgcb` (`WINEPREFIX` == `MGFXC_WINE_PATH`), ~1.9 GB |
| Minimal env | `MGFXC_WINE_PATH="$HOME/.wine-mgcb"` (WineHelper sets the rest itself) |
| Android result | **6 succeeded, 0 failed**, `bin/Android/fx/*.xnb` MGFX produced |
| DesktopGL result | **6 succeeded, 0 failed** |
| XboxOne result | 6 succeeded, 0 failed (native bundled DXC, no Wine) |
| Exact error fixed | `MGFXC0001: MGFXC effect compiler requires a valid Wine installation…` / `The type initializer for 'MonoGame.Effect.Compiler.WineHelper' threw an exception.` |

The blocker was **not** Wine itself but the missing Windows-side payload in the prefix:
a Windows `dotnet` runtime, `d3dcompiler_47.dll`, and `C:\fxccs.dll`. The official
Linux/MonoGame setup script that installs these (`fxccs.zip`) is the one at
<https://monogame.net/downloads/net8_mgfxc_wine_setup.sh>; the copy checked into the
MonoGame repo (`Tools/MonoGame.Effect.Compiler/mgfxc_wine_setup.sh`) is **stale and does
not download `fxccs`**.

---

## 1. Why Wine is required (and exactly what it needs)

Decompiling `MonoGame.Framework.Content.Pipeline.dll` (v3.8.5.1),
`MonoGame.Effect.Compiler.WineHelper`, shows the OpenGL/Android effect path calls:

```
wine64 dotnet c:\fxccs.dll "<Z:\...\in.fx>" <EntryPoint> <profile> <flags> <displayPath> "<Z:\...\out>"
```

`WineHelper`'s static ctor requires **all** of:

1. `which wine64` (or `which wine`) succeeds — Linux prefers `wine64`.
2. `which winepath` succeeds.
3. `MGFXC_WINE_PATH` is set and non-empty (used as `WINEPREFIX`).

It then sets, itself, before every invocation:

```
WINEARCH=win64
WINEDLLOVERRIDES=d3dcompiler_47=n,explorer.exe=e,services.exe=f
WINEPREFIX=$MGFXC_WINE_PATH
WINEDEBUG=-all
MVK_CONFIG_LOG_LEVEL=0
```

It does **not** touch `DISPLAY`. The prefix itself must contain:

| file | purpose |
|---|---|
| `drive_c/windows/system32/dotnet.exe` (+ SDK/`shared` trees) | the `dotnet` muxer run inside Wine |
| `drive_c/windows/system32/d3dcompiler_47.dll` | native D3DCompiler (overridden to native) |
| `drive_c/fxccs.dll` (+ `fxccs.deps.json`, `SharpDX*.dll`) | the actual HLSL→DX9-bytecode shim |

---

## 2. Provisioning (reproducible)

Install (all from Ubuntu universe; no WineHQ repo was needed):

```bash
sudo apt-get install -y --no-install-recommends wine wine64 p7zip-full curl
```

Then run the equivalent of `https://monogame.net/downloads/net8_mgfxc_wine_setup.sh`,
parameterised to `$HOME/.wine-mgcb` (the script hard-codes `~/.winemonogame`). The
working script used here is preserved at `/tmp/opencode/provision-wine-mgcb.sh`; the
essential steps are:

```bash
export WINEARCH=win64 WINEPREFIX="$HOME/.wine-mgcb" WINEDEBUG=-all
unset DISPLAY
wine64 wineboot -i                                        # non-interactive prefix init

# 1) Windows .NET SDK 8 (win-x64) into system32 -> dotnet.exe
curl -fsSL -o /tmp/dotnet-sdk.zip \
  https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.401/dotnet-sdk-8.0.401-win-x64.zip
7z x /tmp/dotnet-sdk.zip -o"$WINEPREFIX/drive_c/windows/system32/" -y

# 2) native d3dcompiler_47.dll (extracted from the Firefox 62.0.3 installer)
curl -fsSL -o /tmp/firefox.exe \
  "https://download-installer.cdn.mozilla.net/pub/firefox/releases/62.0.3/win64/ach/Firefox%20Setup%2062.0.3.exe"
7z e /tmp/firefox.exe "core/d3dcompiler_47.dll" \
  -o"$WINEPREFIX/drive_c/windows/system32/" -aoa

# 3) fxccs.dll -> C:\
curl -fsSL -o "$WINEPREFIX/drive_c/fxccs.zip" https://monogame.net/downloads/fxccs.zip
7z x "$WINEPREFIX/drive_c/fxccs.zip" -o"$WINEPREFIX/drive_c/" -y
```

`fxccs.zip` (182 KB) expands to `fxccs.dll` (5.6 KB), `fxccs.deps.json`,
`SharpDX.dll`, `SharpDX.D3DCompiler.dll`, `fxccs.runtimeconfig.json` (net8.0,
`rollForward: Major`).

### Headless verification

Wine runs a console app with **no X server**. All of the following printed `ok`:

```bash
env -u DISPLAY  wine cmd /c echo headless-ok      # truly headless
DISPLAY=:99     wine cmd /c echo bad-display-ok   # stale/broken DISPLAY
DISPLAY=:0      wine cmd /c echo live-display-ok  # live desktop session
env -u DISPLAY WAYLAND_DISPLAY=wayland-0 wine cmd /c echo wayland-ok
```

So `DISPLAY` does **not** need to be unset; it is not a source of failure. (The commands
below unset it only to model a CI host.)

---

## 3. Minimal working environment + command

```bash
source tools/scripts/env.sh   # DOTNET_ROOT + PATH only
export MGFXC_WINE_PATH="$HOME/.wine-mgcb"              # the only Wine-related var needed

cd /tmp/opencode/fxandroid
mgcb F.mgcb /platform:Android /outputDir:bin/Android /intermediateDir:obj/Android
```

`WINEARCH`, `WINEPREFIX`, `WINEDLLOVERRIDES`, `WINEDEBUG` and `MVK_CONFIG_LOG_LEVEL` are
set by `WineHelper` at run time — do **not** set them by hand (a stray `WINEARCH=win32`
in the environment would be overridden anyway).

### Outputs (scratch, `/tmp/opencode/fxandroid`)

```
bin/Android/fx/Sprite.xnb        2173 bytes
bin/Android/fx/Tint.xnb          3087 bytes
bin/Android/fx/SpriteShadow.xnb  2925 bytes
bin/Android/fx/Bloom.xnb         7723 bytes
bin/Android/fx/Blur.xnb          4995 bytes
bin/Android/fx/Ink.xnb           2813 bytes
```

Each is a valid MGFX effect: XNB header platform byte `a` (Android), and the literal
`MGFX` magic at file offset 141 (`XNB a`, then the `Microsoft.Xna.Framework.Content.EffectReader` type string). Platform bytes observed: `a`=Android, `d`=DesktopGL, `O`=XboxOne.

---

## 4. DesktopGL (second OpenGL reference target)

Same Wine path, same env:

```bash
mgcb F.mgcb /platform:DesktopGL /outputDir:bin/DesktopGL /intermediateDir:obj/DesktopGL
# Build 6 succeeded, 0 failed.
```

`/platform:XboxOne` (native bundled `dxc`, **no Wine**) still works as before:
`Build 6 succeeded, 0 failed` (XNB sizes 9.5–13.8 KB).

---

## 5. Running against the real project content

`Dishwasher/Content/Content.mgcb` currently lists **no content items** (the
`#---- Content ----` section is empty), so a plain build emits nothing. This is
expected — the six `.fx` files were staged under `Content/fx/` but not yet wired into the
manifest (another agent may be mid-edit).

The real sources were verified to compile into the project's own output directory with
this command (run from the project `Content/` dir, using only build outputs; **no source
file was modified**):

```bash
cd src/Dishwasher/Content
source tools/scripts/env.sh
export MGFXC_WINE_PATH="$HOME/.wine-mgcb"

mgcb /platform:Android /outputDir:bin/Android \
     /intermediateDir:/tmp/opencode/fxandroid/obj/projreal \
     /profile:Reach /compress:False \
     /build:fx/Sprite.fx /build:fx/Tint.fx /build:fx/SpriteShadow.fx \
     /build:fx/Bloom.fx /build:fx/Blur.fx /build:fx/Ink.fx
```

Result: `Build 6 succeeded, 0 failed`, sources were the real
`Content/fx/*.fx`, and it produced:

```
Content/bin/Android/fx/Sprite.xnb        2173 bytes
Content/bin/Android/fx/Tint.xnb          3087 bytes
Content/bin/Android/fx/SpriteShadow.xnb  2925 bytes
Content/bin/Android/fx/Bloom.xnb         7723 bytes
Content/bin/Android/fx/Blur.xnb          4995 bytes
Content/bin/Android/fx/Ink.xnb           2813 bytes
```

### What the integrator must do

**1. Add the six content entries to `Content.mgcb`** (MGCB's `.mgcb` format uses
`/build:` lines; the leading `/` path is the asset name, derived from the source path
relative to the manifest dir):

```text
#begin fx/Sprite.fx
/build:fx/Sprite.fx
#begin fx/Tint.fx
/build:fx/Tint.fx
#begin fx/SpriteShadow.fx
/build:fx/SpriteShadow.fx
#begin fx/Bloom.fx
/build:fx/Bloom.fx
#begin fx/Blur.fx
/build:fx/Blur.fx
#begin fx/Ink.fx
/build:fx/Ink.fx
```

Once the entries exist, the plain project manifest build works:

```bash
cd src/Dishwasher/Content
export MGFXC_WINE_PATH="$HOME/.wine-mgcb"
mgcb Content.mgcb /platform:Android /outputDir:bin/Android /intermediateDir:obj/Android
```

and the MSBuild `MonoGame.Content.Builder.Task` target
(`RunContentBuilder` in `MonoGame.Content.Builder.Task.targets`, which already passes
`/platform:… /outputDir:… /intermediateDir:… /workingDir:…`) will drive it automatically
for `net8.0-android` builds.

**2. Make `MGFXC_WINE_PATH` available to the build.** The natural place is
`tools/scripts/env.sh` — add:

```bash
export MGFXC_WINE_PATH="$HOME/.wine-mgcb"
```

(Not added here: this agent's write scope was `/tmp/opencode` + `notes/` + build outputs.)

---

## 6. MGCB invocation gotchas hit during this work

* A `.mgcb` file passed on the command line is treated as a **response file**
  (`ParsePreprocessArg` matches `*.mgcb`). Lines starting with `#` are ignored; every
  other line must be a valid command-line option. Content entries therefore must be
  `#begin <source>` + `/build:<source>` — a bare `/fx/Name.fx` line is parsed as a bad
  option ("Unknown option 'x'"/"Invalid value 'true'").
* **Relative `/build:` paths are resolved against the current directory at parse time.**
  When a response file is read, MGCB temporarily `cd`s to that file's directory, so a
  manifest's relative entries resolve next to the manifest. That is why passing a
  manifest in `/tmp` pointing at relative paths builds the `/tmp` copies, not the
  project.
* `/outputDir` and `/intermediateDir` become absolute via `Path.GetFullPath` **at parse
  time**, so option order matters; MSBuild passes them absolutely after `/@:…` for this
  reason. `SetWorkingDir` calls `Directory.SetCurrentDirectory` immediately, so the last
  `/workingDir:` wins.

---

## 7. Residual limitations / notes

* **Shader compilation itself has no residual limitation** — all six Android/GL shaders
  compile and emit MGFX, headless.
* Cosmetic Wine quirk: `wine dotnet --version` inside this prefix throws
  `System.ConsolePal.SetConsoleOutputEncoding(...)` → unhandled exception. This is the
  **dotnet CLI's own `--version` code path** and is *not* exercised by the effect
  pipeline: `dotnet c:\fxccs.dll …` loads and runs `fxccs` correctly (12/12 Wine
  invocations per 6-shader build, all exit 0). Ignore it.
* Prefix size ~1.9 GB (Windows .NET SDK dominates). The provisioning downloads
  (`dotnet-sdk.zip` ≈ 280 MB, `firefox.exe` ≈ 40 MB) are not needed after setup and were
  removed from `/tmp/opencode/wineprov`.
* `WineHelper` throws `PlatformNotSupportedException` on non-Unix; a Windows build host
  does not need any of this.
* If Wine were unavailable, the fallback for the *shipping* shaders is `/platform:XboxOne`
  (native DXC) — but that targets SM6/DX12, **not** Android/GL, so it is not a substitute
  for this path.
