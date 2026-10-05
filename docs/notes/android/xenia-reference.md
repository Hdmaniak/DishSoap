# Xenia Canary reference attempt — The Dishwasher: Dead Samurai (58410902)

**Date:** 2026-10-02
**Goal:** boot the original Xbox 360 XBLA title under Xenia Canary on Linux to capture
ground-truth reference frames (title / main menu / gameplay) at 1280×720 for shader and
colour/contrast fidelity work on the Android MonoGame port.

## Verdict (TL;DR)

**Xenia Canary cannot render this title on Linux. This is a genuine emulator limitation,
not a configuration problem — no reference frame can be captured from Xenia.**

- Xenia **does** install, start on Vulkan, mount the STFS/LIVE package, identify the title,
  and boot the guest as far as the XNA/.NET Compact Framework runtime.
- It then **hangs immediately after loading `NetCFUserMode.dll`**, at the .NET CF CLR's
  user-mode environment bootstrap (`KeCreateUserMode`), which Xenia does not implement.
- This is the **documented, "WONTFIX"** XNA status for this exact title (see §7).
- Result: **zero rendered game frames**. The only screenshots obtainable show a black
  guest surface with the Xenia UI/title bar.

This matches the project's own finding (`HANDOFF.md`): the game ships as managed IL and the
console CLR JITs it; Xenia stubs the user-mode CLR, so there is nothing Xenia can execute.

---

## 1. Install method / version

| Item | Value |
|---|---|
| Method | Snap (preferred route), `snap install xenia-canary --candidate` |
| Name / version | `xenia-canary f65fda3` (rev **82**, channel `latest/candidate`, publisher `anirudhsevugan`) |
| Commit | `f65fda3320b963846bf5c8bb16c1d5f1c6cffb67` ("canary_experimental@f65fda332, Oct 1 2026") |
| Install path | `/snap/xenia-canary/82` (`/snap/xenia-canary/current` symlink) |
| Binary | `/snap/xenia-canary/82/bin/xenia_canary` (14.7 MB) |
| Launcher | `~/xenia/run.sh` (see §3) |
| GPU | Vulkan / RADV, **AMD Radeon RX 7900 XTX (NAVI31)**, device index **1** |
| Data roots | `XDG_DATA_HOME=/tmp/opencode/xenia-data/Xenia` (content / cache_host kept off the full `/`) |

The GitHub tar release was **not needed** — the snap installed cleanly. The snap's own
launcher is broken on this host (see §6), so the snap's binary is invoked directly.

## 2. Configuration used

Config file: `/tmp/opencode/xenia-data/Xenia/xenia-canary.config.toml`

```toml
[GPU]
gpu = "vulkan"
vulkan_device = 1            # RX 7900 XTX (default device 0 is the Raphael iGPU)

[Content]
license_mask = 1             # XBLA full-version license

[Display]
fullscreen = false
vsync = false

[General]
discord = false

[HID]
keyboard_mode = 1            # was 0 (disabled); enabled keyboard → controller mapping

[Storage]                    # left empty; XDG_DATA_HOME relocates everything to /tmp
[UI]
window_size_x = 1280
window_size_y = 720
```

`GDK_BACKEND=x11` is set in the launcher (required — see §6).

## 3. Exact launch command

`~/xenia/run.sh`:

```bash
#!/usr/bin/env bash
export XDG_DATA_HOME=/tmp/opencode/xenia-data   # keep cache/content off the 99%-full /
export GDK_BACKEND=x11                          # Xenia's GTK window supports only X11, not Wayland
export DISPLAY="${DISPLAY:-:0}"
exec /snap/xenia-canary/current/bin/xenia_canary "$@"
```

Invoked with either input:

```bash
# STFS/LIVE package (got furthest):
~/xenia/run.sh "<extracted>/58410902/000D0000/BF12F19032010E0A01D658FDBD4D49505327249858"

# Extracted XEX + content tree (same wall, plus content-device failures):
~/xenia/run.sh $DISHWASHER_ASSETS_DIR/default.xex
```

## 4. Boot trace (evidence, from `xenia.log`/stdout)

The STFS package (`LIVE` magic confirmed) is mounted correctly and the chain proceeds:

```
i> Vulkan instance API version 1.4.341 ...
w> Available Vulkan physical devices (use 'vulkan_device' to force one):
w>  * 0: AMD Ryzen 7 7800X3D 8-Core Processor (RADV RAPHAEL_MENDOCINO)
w>  * 1: AMD Radeon RX 7900 XTX (RADV NAVI31)
i> Vulkan device 'AMD Radeon RX 7900 XTX (RADV NAVI31)': API 1.4.335 (1.3 used), driver version 0x6800008
i> VulkanPresenter: Created 1280x720 swapchain with format 44, color space 0, presentation mode 0

i> Loading module GAME:\default.xex
i> Module \Device\Content\1\HostLoader.dll:        (native PPC loader)
i> Module \Device\Content\1\default.xex:           (TitleLauncher.pe, XNA launcher)
i> Module \Device\Content\1\Runtime\v2.0\RuntimeHost.xex:   (the .NET CF CLR + XNA native layer)
     RuntimeHost_CreateInstance - 821E3758

   F 82120944 8264D8DC 058 (  88) !! KeCreateUserMode        <-- import in RuntimeHost, UNIMPLEMENTED
!> F800002C undefined extern call to 8264D8DC KeCreateUserMode
i> Module \Device\Content\1\Runtime\v2.0\NetCFUserMode.dll:
   ...  <-- log stops here; never proceeds
```

Window title (proof Xenia mounted + identified the title), 1280×720 guest swapchain:
`Xenia-canary (canary_experimental@f65fda332 on Oct 1 2026) | [58410902 v0.0.1.3] The Dishwasher <Vulkan - FBO - ALSA>`

After the hang, the process stays alive but is completely idle:
- Guest main XThread blocked in `hrtimer_nanosleep`; all other guest threads blocked in `futex`.
- No log growth, no RSS change, no swapchain/present activity, no frame change for 3+ minutes
  (verified by repeated window captures → identical MD5).

## 5. What was captured (paths + what each shows)

All under `<dev-workspace>/android/notes/reference/xenia/`:

| File | What it shows |
|---|---|
| `xenia_window_with_title.png` | Decorated Xenia window; **title bar proves the mounted title** (`[58410902 v0.0.1.3] The Dishwasher <Vulkan - FBO - ALSA>`); guest client area is **black**. |
| `xenia_window_boot_black.png` | Client-area capture of the STFS run: black guest surface + Xenia menu bar. |
| `xenia_window_license1_black.png` | Same with `license_mask=1`: still black. |
| `xwininfo_xenia_window.txt` | `xwininfo` tree — window ID/title/size evidence. |
| `xenia_game2_boot_hang.log` | Full STFS boot log: Vulkan OK → mount → CLR → hang. |
| `xenia_license1_boot_hang.log` | Same with `license_mask=1`. |
| `xenia_extractedxex_boot_hang.log` | Extracted-XEX route: same hang + `Failed to initialize device` (content packages). |
| `xenia_game1_vulkan_fail.log` | Initial run showing `Failed to create a Vulkan instance: ErrorIncompatibleDriver` (diagnosed in §6). |

**No title-screen, main-menu, or gameplay frame exists.** The guest never reaches XNA
`Game.Run()`; it dies inside CLR startup, long before any 3D/2D content is drawn. The
1280×720 colour/contrast look of the game therefore **could not be sampled via Xenia**.

## 6. Host-specific gotchas found (worth keeping)

1. **Snap wrapper is broken** — `snap run xenia-canary` fails with
   `error while loading shared libraries: libpulsecommon-16.1.so: cannot open shared object file`.
   The snap *ships* that file, but at `.../usr/lib/x86_64-linux-gnu/pulseaudio/`, which snapd's
   sanitised `LD_LIBRARY_PATH` does not include. Workaround: run the snap binary directly against
   host libraries.
2. **Do NOT prepend the snap's lib dir** — doing so puts the snap's older `libwayland-client`
   ahead of the host's, so Mesa 26's `libvulkan_radeon.so` fails with
   `undefined symbol: wl_fixes_interface`, the loader drops every ICD, and Xenia reports
   `Failed to create a Vulkan instance: ErrorIncompatibleDriver`. Running the binary with a clean
   (host) environment fixes Vulkan.
3. **Wayland session needs `GDK_BACKEND=x11`** — otherwise
   `GTKWindow: The window system of the GTK window is not supported by Xenia` (no presentation).
4. **Default Vulkan device is the iGPU** (index 0 = RADV `RAPHAEL_MENDOCINO`). Set
   `vulkan_device = 1` to use the RX 7900 XTX.
5. **`/dev/shm` is not `noexec`** on this host (`rw,nosuid,nodev,...`), and the
   `Unable to allocate code cache generated code storage` (#549) error was **never hit** — the
   `[Linux] use_shm_open` workaround was not required.
6. **Disk hygiene:** everything stayed under `$HOME/xenia`, `/tmp/opencode`, and the notes dir;
   archives were not downloaded; Xenia's cache/content were redirected to `/tmp` (tmpfs).
   Root filesystem free space changed 3.8 GB → 4.6 GB; never approached 100%.
   The stray `~/.local/share/Xenia` created by the first default run was removed.

## 7. Root cause / external confirmation

The blocker is Xenia's lack of a `.NET Compact Framework` user-mode environment. Both the
technical signal and the public compatibility record are unambiguous:

- Local signal: `!! KeCreateUserMode` (unimplemented kernel export) →
  `undefined extern call to 8264D8DC KeCreateUserMode` → load `NetCFUserMode.dll` → hang.
- Xenia game-compatibility **Issue #642 — `58410902 - The Dishwasher: Dead Samurai`**:
  labels **`state-crash-xna-WONTFIX`** + **`tech-engine-xna`**,
  *"Title crashes because it is using XNA. For the time being, this is WONTFIX."*
  (and the issue body notes *"Crashes because Xenia isn't implicitly loading HostLoader.dll -
  but after that this becomes the same as #199."* — our STFS run **does** load `HostLoader.dll`,
  so only the #199 wall remains).
- Xenia **Issue #199** (XNA/Terraria): *"This game is trying to create a user-mode environment,
  which we barely support. We're probably not going to see this game working on Xenia..."*
- Community corroboration: the game *"creat[es] its own .NET environment when launched on the
  Xbox 360 hardware, which doesn't work on Xenia."*

This is independent confirmation of `HANDOFF.md`: the title is managed IL executed by the
console CLR, and Xenia supplies no working user-mode CLR.

## 8. Recommendation for the port

Do **not** budget further time on Xenia for visual ground truth — the outcome is a documented
WONTFIX and there is no configuration/patch path on Linux. For colour/contrast fidelity, rely on:
- the game's own assets already converted under `port/assets` / `assets-clean` (textures, `.fx`),
- the existing `android/notes/reference/dishwasher-reference.jpg`,
- any prior capture/colour analysis in `colour-grade-fix.md` / `shader-feasibility.md`.

If real-hardware reference is ever required, a physical Xbox 360 (or a different, CLR-aware
reimplementation) is the only route; Xenia Canary `f65fda3` is a dead end for this title.

## 9. Reproduction (short)

```bash
sudo snap install xenia-canary --candidate
# (optional) sudo snap connect xenia-canary:joystick
~/xenia/run.sh "<extracted>/58410902/000D0000/BF12F19032010E0A01D658FDBD4D49505327249858"
# Observed: Vulkan OK + STFS mount + HostLoader/RuntimeHost/NetCFUserMode load,
#           then "undefined extern call to ... KeCreateUserMode" and a permanent hang.
```
