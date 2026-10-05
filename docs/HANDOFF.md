# Handoff: Porting *The Dishwasher: Dead Samurai* (Xbox 360 XBLA) to Android ARM64

**Date:** 2026-10-01
**Status:** ReXGlue static-recompilation route is a **dead end for this title** (structural, not a bug).
Two independent analyses reached the same conclusion. Evidence below is reproducible.

---

## 1. What we are trying to achieve

Produce a native Android `arm64-v8a` APK of **The Dishwasher: Dead Samurai** (Xbox 360 XBLA,
Title ID `58410902`, Media ID `04ED9EE0`), playable with an Xbox 360 gamepad.

Initial plan (per the original brief): use **ReXGlue SDK v0.10.0** to statically recompile the
Xbox 360 PowerPC XEX into C++, cross-compile to ARM64 with the NDK, wire Vulkan + SDL3 input,
package and deploy via ADB.

Input artifact: `Dishwasher, The - Dead Samurai (World) (XBLA).zip` (61.9 MB, "World" ROM).

---

## 2. THE SHOWSTOPPER (one paragraph)

**This is not a native Xbox 360 title. It is an XNA Game Studio / .NET Compact Framework 3.5
game.** The actual game logic ships as **IL bytecode** in managed assemblies
(`game.exe.xex`, `Microsoft.Xna.Framework*.dll.xex`, `mscorlib.dll.xex`, …), and the console's
CLR (`RuntimeHost.xex` + `NetCFUserMode.dll`) **JIT-compiles that IL to PowerPC at runtime**.
ReXGlue is an *ahead-of-time* recompiler with **no interpreter and no JIT fallback**: it maps
static guest addresses to host C++ functions. There is nothing to translate (the game is IL,
not PPC), and recompiling the CLR itself would produce an ARM64 program that *emits and executes
PowerPC machine code at runtime* — impossible on ARM64. The two halves fail independently.

---

## 3. Hard evidence (reproducible)

### 3.1 The distribution is an XNA / .NET CF package

Extracted from the STFS/LIVE package (reXGlue has no extract command; the SDK's own
`StfsContainerDevice` parser was used). 560 files, ~120 MB. Key modules:

| Module | Role |
|---|---|
| `default.xex` | Launcher only (`TitleLauncher.pe`); native PPC |
| `HostLoader.dll` | Native PPC; string `RuntimeHost.xex`, `RuntimeHost_CreateInstance` |
| `RuntimeHost.xex` | **The .NET CF CLR + XNA native layer** (native PPC) |
| `NetCFUserMode.dll` | .NET CF native support (611 exports) |
| `game.exe.xex` | **The game — managed IL** |
| `Runtime/v2.0/{mscorlib,System,System.Xml,System.SR,Microsoft.Xna.Framework,Microsoft.Xna.Framework.Game}.dll.xex` | **Managed IL framework/BCL** |
| `Runtime/v2.0/*.nlp.xex` (×10) | BCL codepage/sort data |

Build stamp found in the assembly's native stub PDB path:
`d:\XnaNightly\E4A\2008.06.29.14h35m\private\gamertools\hosting\Platform\Xenon\StubBin\Xbox 360\Release\StubBin.pdb`
→ **XNA Game Studio 3.x era, .NET CF 3.5**.

### 3.2 `game.exe.xex` is a managed assembly, not native code

Dump of the decrypted/decompressed module (tool: `/tmp/opencode/xex_dump.cpp`, built against the
SDK runtime). The native XEX wrapper contains **no executable section at all**:

```
=== module: game.exe.xex ===
base=0x83240000 image_size=0x120000
PE sections:
  .rdata   va=0x83240200 vsz=0x000000A7 flags=0x40000040
  .data    va=0x83250000 vsz=0x00000004 flags=0xC0000040
Import libraries (0):
SIG BSJB         count=1     <- .NET metadata root magic
SIG #Strings     SIG #~   SIG #US   SIG #GUID   SIG #Blob     <- metadata streams
SIG mscoree      SIG _CorExeMain                               <- managed entry point
```

The real payload is an embedded I386 PE at image offset `0x20000` with an
`IMAGE_COR20_HEADER` (file `0x21008`):

```
cb=0x48  runtime=2.5  MetaData RVA=0x7F210 size=0x40180
Flags = 0x00000001        <- COMIMAGE_FLAGS_ILONLY  (pure IL, no native code)
EntryPointToken = 0x060008CB   <- MethodDef token (managed entry)
Metadata root @ 0x9E210: 42 53 4A 42 ("BSJB"), version "v2.0.50727"
```

Identical findings for the six framework DLLs (`Flags = 0x9` = `ILONLY|STRONGNAMESIGNED`).

### 3.3 The runtime is a CLR that JITs IL

`RuntimeHost.xex` contains CLR internals: `ILFormatDecode::IsModifierPresent( dstToken ) == false`,
the assembly probe list (`mscorlib.dll` / `mscorlib.dlx`, `system.dll`, `system.sr.dll`,
`microsoft.xna.framework.dll`, …), reflection types (`RuntimeMethodInfo`, `RuntimeConstructorInfo`,
`AssemblyName`, `CustomAttribute`), and the metadata stream-name literals `#~`/`#Strings`/`#US`/`#GUID`/`#Blob`.
(It also contains the D3DX/HLSL compiler — the XNA native graphics layer.)

**No `.dlx` (native image) exists anywhere in the package**, although the CLR probes for them →
the runtime takes the IL path, i.e. **JIT**.

> Measured: all `ILONLY` flags, COR20 headers, IL method bodies, metadata streams, PE section
> layout, imports/exports, and the absence of native images.
> Inferred (strong basis): the .NET CF execution engine JITs to the target CPU (PowerPC on 360).
> Not established: whether it is pure-JIT or a hybrid interpreter/JIT — the conclusion is
> unchanged either way.

---

## 4. Dependency graph

```
default.xex (TitleLauncher) ──► HostLoader.dll (export ord1 = 0x820B1048)
      └─► RuntimeHost.xex  (the CLR + XNA native)
               └─► JITs  game.exe.xex + mscorlib / System / System.Xml / System.SR /
                         Microsoft.Xna.Framework[.Game]      <- all managed IL
      NetCFUserMode.dll (native .NET CF support)
```

Minimal native set to stand up the runtime: `default.xex` + `HostLoader.dll` +
`RuntimeHost.xex` + `NetCFUserMode.dll`. The **game** additionally needs the 7 IL assemblies,
**none of which are recompilable**.

---

## 5. Why ReXGlue cannot bridge this

- **Game side:** the logic is IL. A PPC→C++ recompiler has nothing to translate.
- **Runtime side:** recompiling the CLR to ARM64 yields code that generates PPC machine code.
  ReXGlue dispatches by *static guest address* → host function; JIT-emitted code has no static
  mapping. Its README is explicit that it does not interpret or JIT at runtime.
- **What would be required instead:** a runtime IL execution engine (guest interpreter/JIT in the
  host) or replacing the guest CLR with a host .NET runtime, plus a reimplemented XNA platform
  layer, guest-module export registration, loader/`mscoree`/reflection/GC semantics — i.e. a new
  runtime, not a port.

---

## 6. Recommended alternative (the practical route)

**Extract the managed assembly and run it on a host CLR with an XNA reimplementation.**

- The package contains a standard .NET CF 3.5 managed assembly (`game.exe`, IL, at image offset
  `0x20000`). It is normal IL and can be loaded by a desktop CLR.
- XNA reimplementations: **FNA** (faithful XNA 4.0 API on SDL3; desktop-focused) or
  **MonoGame** (XNA 4.0 API; **has an official Android backend**).
- Console-specific subsystems must be shimmed: **GamerServices / Xbox LIVE** (the assembly
  references `Microsoft.Xna.Framework.GamerServices` and `.Net`), storage devices, achievements,
  and input mapping to a gamepad.

### Reusable work already completed (applies to either route)

| Item | Path / status |
|---|---|
| Host toolchain | clang 20.1.8, cmake 4.2.3, ninja 1.13.2 |
| Android toolchain | NDK r28b `$HOME/android-ndk-r28b`; SDK 35 + build-tools 35 + JDK 21 `$ANDROID_SDK_DIR` |
| ReXGlue SDK v0.10.0 | built for host + **cross-compiled for Android arm64 (795/795 steps, 0 errors)** — only relevant if another *native* 360 title is attempted |
| Android APK skeleton | `<android-app>` (package `com.recomp.dishwasher`, SDLActivity, Gradle 8.12/AGP 8.7.3, keystore, `build-apk.sh`, `deploy.sh`) — **builds, signs, installs on the target device** |
| Device | Samsung SM-A525F (Galaxy A52), Android 14 / API 34, **Adreno 618, Vulkan 1.1.128** (probed) |
| Managed payload extractor | STFS extractor + `/tmp/opencode/xex_dump.cpp` (decrypts XEX modules via SDK loader) |

---

## 7. Open questions for an expert

1. **API gap:** this is **XNA 3.x / .NET CF 3.5**. FNA and MonoGame target the **XNA 4.0** API.
   What breaks in a 3.x→4.0 migration (GraphicsDevice, SpriteBatch, ContentManager, XACT audio)?
2. **Running a CF 3.5 assembly on desktop .NET 8 / Mono:** retargeting/assembly-binding strategy;
   are there CF-specific P/Invokes or `mscorlib` shims to replace?
3. **Content:** are the `gfx/`, `sfx/`, `Resources/` assets XNA `.xnb`? Can FNA/MonoGame consume
   them as-is, or must the content pipeline be re-run?
4. **Xbox LIVE surface:** how much of `GamerServices`/`Net`/`StorageDevice`/achievements does the
   game actually use, and what is the minimal shim?
5. **Is there prior art** for an XNA 3.x Xbox 360 title running on MonoGame/FNA + Android?

---

## 8. Reproduce / verify

```bash
# SDK CLI (built)
<rexglue-sdk>/out/install/linux-amd64/bin/rexglue --version   # 0.10.0.0-dev.unknown

# Extracted game data (STFS unpacked)
ls <extracted-package>/assets

# Decrypt + dump a module (shows the COR20/BSJB/_CorExeMain evidence)
/tmp/opencode/xex_dump <extracted-package>/assets game.exe.xex /tmp/opencode/verify
strings -n 8 /tmp/opencode/verify/game.exe.xex.image.bin | grep Xna.Framework

# Full analyses
<dev-workspace>/android/notes/xna-netcf-feasibility.md     # this verdict, in detail
<dev-workspace>/android/notes/android-support-audit.md     # Android platform gaps (solved)
<dev-workspace>/android/notes/arm64-runtime-audit.md       # x86/fiber/SIMD audit (solved)
<dev-workspace>/android/notes/vulkan-mobile-audit.md       # Adreno 618 probe results
```

**Bottom line:** ReXGlue is the wrong tool for this specific title. The game must be treated as a
managed XNA application and run on a .NET/XNA-compatible runtime (FNA/MonoGame), not recompiled.
