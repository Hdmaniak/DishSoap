# XNA / .NET Compact Framework Feasibility — *The Dishwasher: Dead Samurai* (XBLA `58410902`)

- **Author:** .NET CF / XNA Feasibility Specialist (subagent)
- **Date:** 2026-10-01
- **ReXGlue SDK:** v0.10.0.0-dev.unknown (`<rexglue-sdk>`)
- **Assets:** `<extracted-package>/assets` (extracted from STFS/LIVE package)
- **Verdict:** **NOT recompilable with ReXGlue as-is, and not by the static-AOT model even with the
  guest-DLL import fix.** The game logic is managed IL, not PowerPC machine code. The native runtime
  JIT-compiles that IL to PowerPC at runtime, and ReXGlue has no interpreter / JIT / dynamic
  recompiler fallback.

---

## 0. Bottom line (read this first)

| Question | Answer |
|---|---|
| Are the shipped title modules native PowerPC? | **Mixed.** 4 of 20 XEX modules contain native PPC; 7 contain **managed IL only**; 8 are data tables. |
| Is the game logic (`game.exe.xex`) native PPC? | **No. It is a pure-IL .NET assembly** (`IMAGE_COR20_HEADER.Flags = 0x1 ILONLY`, `_CorExeMain`/`mscoree.dll`, metadata `v2.0.50727`). |
| Does the runtime JIT or AOT the IL? | **JIT.** The IL is JIT-compiled to PowerPC at runtime by the CLR inside `RuntimeHost.xex`. There are **no** AOT/native images (`.dlx`) shipped. |
| Can ReXGlue recompile the game? | **No.** ReXGlue is a static PPC→C++/ARM64 ahead-of-time recompiler. There is no PPC code for the game to recompile, and the runtime-generated PPC cannot be statically mapped. |
| Can ReXGlue recompile the *native* modules (`default.xex`, `HostLoader.dll`, `RuntimeHost.xex`, `NetCFUserMode.dll`)? | Technically **yes** (they are real PPC; `default.xex` + `HostLoader.dll` already generate + link), but this is useless on its own: the recompiled CLR would still JIT IL → **PPC** and try to execute that on ARM64. |
| Realistic port path | Extract the managed assemblies and run them on a **host .NET runtime + FNA/MonoGame** (ARM64), reimplementing the XNA platform layer. This is *not* ReXGlue. See §9. |

---

## 1. Method and tooling (all evidence reproducible)

1. **XEX decryption/decompression via the SDK itself.** Every asset module is an `XEX2` container
   with `file_format.encryption = NORMAL(1)` and `compression = NORMAL(2)` (LZX) — except
   `game.exe.xex` which is `BASIC(1)`. Raw `grep` for `.NET` signatures on the on-disk bytes finds
   nothing because the payload is AES/LZX-encrypted. A probe
   (`/tmp/opencode/xex_dump.cpp`, linked against `librexruntime.so`) was written that calls
   `Runtime::LoadUserModule()` — the exact path used by `rexglue codegen` — to decrypt, decompress
   and load each module, then dumps the guest image + PE sections + XEX import/export tables.
2. **PE / COR20 / metadata parsing.** `/tmp/opencode/pe_parse.py` walks every `MZ` candidate in each
   decrypted image and parses the PE32 headers, section table, data directories and the
   `IMAGE_COR20_HEADER` (CLR directory, index 14).
3. **PPC disassembly.** Bundled `tools/binutils/powerpc-none-elf-objdump` (GNU binutils 2.24). Note:
   the bundled target decodes raw binary as **little-endian**, so each 4-byte word was byte-swapped
   (`/tmp/opencode/bswap.py`) before disassembling. Decoding to real big-endian PPC is confirmed by
   sane prologues (`mflr r12; stw r12,-8(r1); stwu r1,-112(r1); lis; mr; bl`).
4. **String / signature scans** of the decrypted images (`BSJB`, `#~`, `#Strings`, `#US`, `#GUID`,
   `#Blob`, `_CorExeMain`, `mscoree`, `#NativeImage`, `ReadyToRun`, `ngen`, `JIT`, …).
5. **SDK source review** of `src/system/xex_module.cpp`, `src/codegen/phase_register.cpp`,
   `src/codegen/project_recompiler.cpp`, `src/system/{export_resolver,user_module,kernel_module}.cpp`.
6. **Cross-check against vendor documentation** for the JIT question (§4).

---

## 2. Per-module verdict table

| Module (guest path) | XEX flags | Outer PE machine | Executable code | CLR/IL | Verdict |
|---|---|---|---|---|---|
| `default.xex` | `0x01` TITLE | `0x01F2` PowerPC | `.text` 0x39384 @0x82010000; entry 0x82015B70 | none | **Native PPC** (TitleLauncher stub) |
| `HostLoader.dll` | `0x09` DLL | `0x01F2` PowerPC | `.text` 0x189E4 @0x820A1000; entry 0x820B1EE8 | none | **Native PPC** (loads the CLR) |
| `Runtime/v2.0/RuntimeHost.xex` | `0x09` DLL | `0x01F2` PowerPC | `.text` 0x46E49C + **9 executable `.embsec_` sections**; entry 0x8225C488 | contains the **CLR** (native) | **Native PPC** (CLR + XNA native layer) |
| `Runtime/v2.0/NetCFUserMode.dll` | `0x09` DLL | `0x01F2` PowerPC | `.text` 0x43C60; entry 0x827A1A08 | none | **Native PPC** (611 exports, .NET CF native support) |
| `game.exe.xex` | `0x09` DLL | `0x01F2` **stub** + embedded `0x014C` I386 | none in stub | **COR20 `flags=0x1` ILONLY**, entry token `0x060008CB`, `_CorExeMain`/`mscoree.dll`, MD `v2.0.50727` | **Managed IL only** (the game) |
| `Runtime/v2.0/mscorlib.dll.xex` | `0x09` DLL | `0x01F2` stub + `0x014C` | none | COR20 `flags=0x9` ILONLY\|STRONGNAMESIGNED | **Managed IL only** |
| `Runtime/v2.0/System.dll.xex` | `0x09` DLL | `0x01F2` stub + `0x014C` | none | COR20 `flags=0x9` | **Managed IL only** |
| `Runtime/v2.0/System.Xml.dll.xex` | `0x09` DLL | `0x01F2` stub + `0x014C` | none | COR20 `flags=0x9` | **Managed IL only** |
| `Runtime/v2.0/System.SR.dll.xex` | `0x09` DLL | `0x01F2` stub + `0x014C` | none | COR20 `flags=0x9` | **Managed IL only** |
| `Runtime/v2.0/Microsoft.Xna.Framework.dll.xex` | `0x09` DLL | `0x01F2` stub + `0x014C` | none | COR20 `flags=0x9` | **Managed IL only** |
| `Runtime/v2.0/Microsoft.Xna.Framework.Game.dll.xex` | `0x09` DLL | `0x01F2` stub + `0x014C` | none | COR20 `flags=0x9` | **Managed IL only** |
| `Runtime/v2.0/*.nlp.xex` (10 files) | `0x09` DLL | `0x01F2` stub | none | none | **Data tables** (BCL codepages/sorting); not code |

> 21 XEX modules total: **4 native PPC** (`default.xex`, `HostLoader.dll`, `RuntimeHost.xex`,
> `NetCFUserMode.dll`), **7 managed IL** (`game.exe.xex` + 6 framework DLLs), **10 data-only**
> (`.nlp.xex`).

---

## 3. Hard evidence per module

### 3.1 `game.exe.xex` — a managed executable (IL), not PPC

Decrypted image (1,179,648 bytes) contains **two** PEs:

```
PE@0x0      machine=0x01F2 (PowerPC)  nsec=2  sections .rdata(0xA7) .data(4)   <- XNA "StubBin" native stub
PE@0x20000  machine=0x014C (I386)     nsec=3  sections .text(.rsrc,.reloc)     <- the managed assembly
```

The outer stub's PDB path proves what it is:

```
d:\XnaNightly\E4A\2008.06.29.14h35m\private\gamertools\hosting\Platform\Xenon\StubBin\Xbox 360\Release\StubBin.pdb
```

`IMAGE_COR20_HEADER` at file offset `0x21008` (RVA `0x2008`):

```
cb                 = 72 (0x48)
Major/MinorRuntime = 2.5
MetaData RVA/size  = 0x0007F210 / 0x00040180
Flags              = 0x00000001        <-- COMIMAGE_FLAGS_ILONLY
EntryPointToken    = 0x060008CB        <-- MethodDef table token => managed entry point
Resources          = 0x0007E1B8 / 0x00001056
```

Metadata root at file offset `0x9E210`:

```
0009e210: 4253 4a42 0100 0100 0000 0000 0c00 0000  BSJB............
0009e220: 7632 2e30 2e35 3037 3237 0000 0000 0500  v2.0.50727......
```

Immediately after the COR20 header are real **IL method bodies**, e.g. at `0x21050`:

```
00021050: 1e 16 80 0d 00 00 04 2a   tiny IL header 0x1e (7-byte body) ; ... ; 0x2a = ret
00021058: 13 30 03 00 69 00 00 00 01 00 00 11   fat IL header 0x3013, maxstack 3, codesize 0x69, locals 0x11000001
```

And the managed entry import at `0x20E462`: `_CorExeMain`, `mscoree.dll` (the standard .NET PE
native entry stub that hands off to the runtime). The embedded assembly's debug path is
`...\TheDishwasher_Win\obj\XDK\SubmissionESRB\game.pdb`.

**No** `#NativeImage`, **no** `ReadyToRun`, **no** native entry point. → pure IL.

### 3.2 The six framework assemblies — also pure IL

Same shape: a tiny PowerPC `StubBin` PE at offset 0 and a managed I386 PE at offset `0x20000`.

| Module | COR20 flags | Interpreted | MetaData RVA |
|---|---|---|---|
| `mscorlib.dll.xex` | `0x9` = ILONLY \| STRONGNAMESIGNED | 2.5 | `0x6F1DC` |
| `System.dll.xex` | `0x9` | 2.5 | `0x1FCD4` |
| `System.Xml.dll.xex` | `0x9` | 2.5 | `0x82290` |
| `System.SR.dll.xex` | `0x9` | 2.5 | `0x52B94` |
| `Microsoft.Xna.Framework.dll.xex` | `0x9` | 2.5 | `0x4C2F4` |
| `Microsoft.Xna.Framework.Game.dll.xex` | `0x9` | 2.5 | `0x8D6C` |

All metadata roots read `BSJB` + `v2.0.50727`. None contains an executable section in its outer PE
(the only outer sections are `.rdata` 0xA7 bytes and a 4-byte `.data`), i.e. **the XEX wrapper has no
PPC code at all** for these modules.

### 3.3 `RuntimeHost.xex` — the CLR (native PPC)

- Outer PE `machine=0x01F2`, 18 sections, `.text` = `0x46E49C` bytes plus **9 executable
  `.embsec_` sections** (statically linked native libraries), entry `0x8225C488`.
- Exports: 1 (`0x8264E46C`); imports: `xam.xex` (87) + `xboxkrnl.exe` (169).
- Disassembly of the entry (byte-swapped) is clean big-endian PPC.
- Strings prove it is the **.NET Compact Framework execution engine**, merged with the XNA native
  layer:
  - `ILFormatDecode::IsModifierPresent( dstToken ) == false` (CLR metadata/IL decoder)
  - `mscorlib.dll` / `mscorlib.dlx`, `system.dll` / `system.dlx`, `system.xml.dll` / `.dlx`,
    `microsoft.xna.framework.dll` / `.dlx`, … (the exact assembly-load probe list)
  - `RuntimeMethodInfo`, `RuntimeConstructorInfo`, `RuntimeFieldInfo`, `RuntimeEventInfo`,
    `CustomAttribute`, `AssemblyName`, `Thread`, `Object`, `String`, `Decimal`, `SZArrayHelper`
  - `RuntimeHost-XdkSubmission-Release.dll`
  - the metadata stream-name literals `#~`, `#Strings`, `#US`, `#GUID`, `#Blob` (found as raw ASCII
    in the image at offsets 0x40xx) — the CLR's metadata reader constants
  - a very large D3DX/HLSL shader-compiler string set (`ID3DXEffectCompiler`, `Microcode Compiler`,
    `SSMUseTranslator`, `xltconvert.cpp`) — the XNA native graphics layer.

### 3.4 `NetCFUserMode.dll` — native PPC

- `machine=0x01F2`, `.text` = `0x43C60`, entry `0x827A1A08`, **611 exports** at `0x827A0000`,
  **0 XEX import libraries**.
- PDB: `M:\XNA\V2\RTM\7318\XBox360\bin\release\NetCFUserMode.pdb`.
- Contains .NET CF reflection native implementations (`RuntimePropertyInfo`, `RuntimeConstructorInfo`,
  `RuntimeMethodInfo`, `RuntimeFieldInfo`, `RuntimeEventInfo`, `CustomAttribute`, `AssemblyName`,
  `Thread`, …) plus localized game strings (System.SR resources).

### 3.5 `default.xex` and `HostLoader.dll` — native PPC

- `default.xex`: 8 sections, `.text` `0x39384` @ `0x82010000`, entry `0x82015B70`, imports
  `xam` (18), `xboxkrnl` (76), and **`HostLoader` ordinal 1** (thunk `0x820490D4`). Strings:
  `GAME.EXE`, `gameinfo.bin`, `GameInfo.xml`, PDB `...TitleLauncher-XdkSubmission-Release.pdb`.
  This is the `TitleLauncher.pe` native bootstrap.
- `HostLoader.dll`: 8 sections, `.text` `0x189E4`, entry `0x820B1EE8`, imports `xboxkrnl` (54),
  `xam` (2); **1 export** at `0x820C89B4`, ordinal 1 → guest address **`0x820B1048`**. Strings:
  `RuntimeHost_CreateInstance`, `RuntimeHost.xex`, PDB `...HostLoader-XdkSubmission-Release.pdb`.
  It is the native loader that loads `RuntimeHost.xex` and creates the CLR instance.
- The import thunk at `0x820490D4` is a standard 8-byte import descriptor + `mtctr r11; bctr`
  (`7d 69 03 a6` / `4e 80 04 20`), i.e. a native PPC import stub — not IL.

### 3.6 `.nlp.xex` files

10 files (`big5`, `bopomofo`, `ksc`, `l_except_3_5`, `l_intl_3_5`, `prc`, `prcp`, `sortkey_3_5`,
`sorttbls_3_5`, `xjis`). Each is a `StubBin` wrapper (outer PE has only `.rdata`/`.data`, no code,
no COR20), i.e. raw BCL codepage/sort data. Not recompilable code.

---

## 4. JIT vs AOT — the decisive question

**Answer: the runtime JIT-compiles IL to PowerPC at runtime. There is no AOT/native image in the
package.**

### 4.1 Measured facts (from the binaries)

1. Every managed assembly has `IMAGE_COR20_HEADER.Flags = ILONLY` (bit 0). Per ECMA-335 / CorHdr.h,
   `ILONLY` means *"the image file is pure IL"* — there is **no native code** in the image.
2. No `#NativeImage` signature, no `ReadyToRun`, no `NATIVE_ENTRYPOINT` flag in any module.
3. The CLR (`RuntimeHost.xex`) probes for `mscorlib.dlx` **and** `mscorlib.dll` (ditto every other
   framework assembly). A `.dlx` (native/AOT image) **is not present anywhere in the package** —
   only the `.dll.xex` (IL) files ship. So the runtime takes the IL path.
4. The CLR itself is present as **native PPC** (`RuntimeHost.xex`, `NetCFUserMode.dll`) — so *some*
   component must turn the IL into runnable code at runtime.

### 4.2 Inference (stated as inference)

Given (1)–(4), the CLR must translate IL at runtime. The .NET Compact Framework execution engine is
documented to contain **a JIT compiler** (not an interpreter):

- Microsoft Learn / .NET CF architecture: *"At the heart of NETCF is the execution engine. This
  contains the usual suspects including the **JIT**, GC, Loader and other service providers."*
- Wikipedia (.NET Compact Framework): *"designed to run … on a special, mobile-device, high
  performance **JIT compiler**."* and *"A version of the .NET Compact Framework is also available for
  the Xbox 360 console … This version is used by XNA Framework to run managed games."*
- Microsoft Learn (managed execution): the JIT *"converts the CIL for that method into **native code**"*
  for the target machine architecture. On Xbox 360 that target is PowerPC.
- Secondary: *"C# source into MSIL bytecode, which the CLR **JIT-compiled to x86 or PowerPC (for Xbox
  360) at runtime**."*

Conclusion: on the console, IL is JIT-compiled to **PowerPC** and executed. The generated code lives
at runtime-determined addresses in executable guest memory.

### 4.3 Why this is fatal to a static recompiler

ReXGlue's model (README): *"Rather than interpreting or JIT-compiling PPC instructions at runtime,
ReXGlue … generates C++ source code ahead of time."* Its runtime dispatches calls by **static guest
address → recompiled host function**. Any of the following is therefore fatal:

- **JIT output** is PPC code produced at runtime at addresses the static tool never saw. A CLR
  recompiled to ARM64 still contains a **PPC** JIT backend, so it would emit PPC and try to execute
  it on ARM64 → illegal instruction. There is no runtime recompiler to catch it.
- **Even an interpreter** would be an enormous new subsystem: ReXGlue has none, and an ARM64
  recompilation of a PPC interpreter would still interoperate with runtime-created CLR stubs,
  reflection, P/Invoke and GC handles that the static dispatcher does not know about.

---

## 5. Module dependency / load graph

Measured from XEX import libraries, export tables and embedded strings:

```
default.xex  (TitleLauncher.pe; XEX_MODULE_TITLE)
   ├─ imports xam.xex        (18)
   ├─ imports xboxkrnl.exe   (76)
   └─ imports HostLoader     ordinal 1  (thunk 0x820490D4)      <-- the unresolved import
          │
          ▼
HostLoader.dll  (XEX_MODULE_DLL; exports 1: ord1 -> 0x820B1048)
   ├─ imports xboxkrnl.exe (54), xam.xex (2)
   ├─ string "RuntimeHost.xex", "RuntimeHost_CreateInstance"
   └─ loads/creates ▼
          RuntimeHost.xex  (XEX_MODULE_DLL; exports 1; the CLR + XNA native)
             ├─ imports xam.xex (87), xboxkrnl.exe (169)
             ├─ probes assemblies: mscorlib.dll(.dlx), System.dll, System.Xml.dll,
             │   System.SR.dll, Microsoft.Xna.Framework.dll, Microsoft.Xna.Framework.Game.dll,
             │   game.exe
             └─ JITs the managed assemblies ▼
                  game.exe.xex  (managed IL exe; mscoree!_CorExeMain)
                     └─ managed refs: mscorlib, System, System.Xml, System.SR,
                                      Microsoft.Xna.Framework[.Game]
          NetCFUserMode.dll  (native, 611 exports, 0 imports) — .NET CF native support,
             resolved by the CLR/HostLoader (no direct XEX import edge)
```

Minimal *native* set to even stand up the runtime: `default.xex` + `HostLoader.dll` +
`RuntimeHost.xex` + `NetCFUserMode.dll`. The **actual game** additionally needs `game.exe.xex` and
the six framework IL assemblies — all of which ReXGlue cannot recompile.

---

## 6. Guest-DLL export resolution (the `sub_820490D4` linker failure)

### 6.1 Root cause (confirmed)

`default.xex` imports `HostLoader.dll` ordinal 1. During `rexglue codegen`:

- `phase_register.cpp` looks up the ordinal in the `ExportResolver` as `"HostLoader.xex"` then
  `"HostLoader"`.
- `ExportResolver` only contains tables registered by the kernel modules at init:
  `xboxkrnl.exe`, `xam.xex`, `xbdm.xex` (`src/kernel/*/…_module.cpp` → `RegisterTable`). **No guest
  DLL module is ever registered.**
- The lookup fails → `REXCODEGEN_ERROR("Cannot resolve ordinal 1 from HostLoader")` → the codegen
  emits a placeholder `sub_<thunk-addr>` (= `sub_820490D4`) with **no body**, referenced from
  `PPCFuncMappings` (`dishwasher_init.cpp`) and `registrar->SetFunction(...)` (`dishwasher_register.cpp`).
- Link fails with one undefined symbol: `sub_820490D4`.

At **runtime** the story is different and already correct: `KernelModule`/`UserModule`
(`src/system/user_module.cpp::GetProcAddressByOrdinal`) resolve a guest DLL's ordinal through the
module's own XEX export table (`XexModule::GetProcAddress(ordinal)`) and either return the guest
address or allocate a dispatcher thunk. So only the **codegen/link** path is broken.

### 6.2 Concrete fix (codegen-side — recommended)

The target of the import already exists as generated code: `HostLoader` ordinal 1 resolves to guest
address **`0x820B1048`**, and `rexglue codegen` has already emitted
`DEFINE_REX_FUNC(sub_820B1048)` / `DECLARE_REX_FUNC(sub_820B1048)` / `SetFunction(0x820B1048, …)`
in the `hostloader` module. So the fix is purely to point the import at it:

1. **Build a guest-export map.** In `project_recompiler.cpp`, after each guest DLL is loaded
   (`LoadUserModule`), read its XEX export table
   (`xex_module()->xex_security_info()->export_table`, format `src/system/util/xex2_info.h`) and
   derive `ordinal → guest function address`. (The SDK already has the decoder:
   `XexModule::GetProcAddress(ordinal)` — enumerate `base..base+count-1`.)
2. **Register it with the `ExportResolver`** under the module base name (`HostLoader`), using a new
   `Export` marker (e.g. `ExportTag::kGuestFunction`, or `Type::kGuestFunction`) so it is
   distinguishable from host/kernel exports. Name the entry `sub_<address>`.
3. **In `phase_register.cpp`**, when the resolved export carries that marker, emit
   `resolvedName = exp->name` (i.e. `sub_820B1048`) **without** the `__imp__` prefix, and register
   the *target address* as a function root so it is guaranteed to be discovered even if it is not
   reachable from the module's entry point (it already is here, but this is not guaranteed in
   general). The generated import function at `0x820490D4` then becomes a tail-call to `sub_820B1048`.
4. **Build-system:** ensure each module's `*_funcs.h` is visible to the other modules' translation
   units (the symbols have external linkage via `REX_EXTERN`/`DECLARE_REX_FUNC`, so only the
   include/link wiring is needed).

Result: `{0x820490D4, sub_820B1048}` in `PPCFuncMappings` and
`registrar->SetFunction(0x820490D4, sub_820B1048)`; the link succeeds and the import does the right
thing (control transfers to the recompiled `HostLoader` export, in the same binary).

Alternative (runtime-side, partial): leave the import stub as regular recompiled code and have the
runtime loader populate the guest IAT (`0x82000400`) via `GetProcAddress(0x820490D4)`; the
`FunctionDispatcher` will then dispatch the dynamic branch target. This avoids a codegen special
case but requires the import thunk to be recompiled as normal code instead of replaced by an import
node, and it relies on the loader path being exercised.

### 6.3 Why the manual stub is a band-aid

`src/import_stubs.cpp` defines `REX_EXTERN(sub_820490D4){ ctx.r3.u64 = 0; }`. It makes the executable
link but **returns a fake success without ever calling `HostLoader` ordinal 1**, so the boot chain
(load `RuntimeHost.xex` → create CLR → run `game.exe`) never starts. It is a temporary link fix, not
a port.

> Note: implementing §6.2 is **necessary but not sufficient** — it only fixes the native
> `default.xex → HostLoader` edge. It does nothing for the managed IL (§4).

---

## 7. What would have to be implemented that the SDK does not do

To actually recompile-and-run *this* title on ReXGlue/ARM64, at minimum:

1. **An IL execution path.** Either
   - a **runtime PPC interpreter/JIT** in the ReXGlue runtime that can execute code the CLR generates
     at runtime (contradicts ReXGlue's static-AOT design), **or**
   - replacing the guest CLR with a **host .NET runtime** and translating the guest CLR's ABI/GC/
     reflection/loader calls onto it. This is no longer ReXGlue recompilation — it is a full runtime
     replacement.
2. **Replacement of the XNA platform layer.** `Microsoft.Xna.Framework.dll(.Game)` and the native
   XNA graphics/audio/input code are IL + static PPC in `RuntimeHost.xex`; they would need a
   GPU/audio/input backend on ARM64/Vulkan.
3. **Guest-DLL export resolution** (§6.2) for the native loader edges.
4. **A working guest loader chain** (the CLR's own loader, `mscoree` semantics, `_CorExeMain`,
   assembly probing, `.dlx` fallback, strong-name checks, GC, thread affinity, sandbox) — none of
   which ReXGlue models.
5. Even the *native* portion is hostile to static recompilation: `RuntimeHost.xex` has **9
   `.embsec_` executable sections** that are MSVC/`/clr`-style embedded native libraries; code in
   them may be reached only through runtime-vtable/reflection paths that the static function scanner
   cannot see.

---

## 8. Manifest proposal

Because the evidence says the **managed** modules are not recompilable, the proposed manifest
`<extracted-package>/dishwasher_manifest.xna.toml` lists only the native PPC modules (which
`rexglue codegen` *can* process) and documents the managed set as excluded. It is provided for
completeness/experimentation, **not** as a path to a working port.

Guest paths use the runtime's canonical form (forward slashes, lower-case is applied by the loader);
`file_path` uses the real on-disk names, which contain literal backslashes (`Runtime\v2.0\...`) and
therefore must be backslash-escaped in TOML.

See the file for the concrete contents.

---

## 9. Recommendation (realistic port path, for the record)

The package ships a **standard managed assembly** (`game.exe`, .NET CF 3.5 / metadata `v2.0.50727`)
that references `Microsoft.Xna.Framework`. The established, practical route for XNA titles is:

1. Extract the managed PE at image offset `0x20000` from `game.exe.xex` (and the framework DLLs) —
   it is a normal IL assembly with a CLR header.
2. Run it on a host CLR and supply the XNA Framework via **FNA** or **MonoGame** (which reimplement
   `Microsoft.Xna.Framework` on SDL/Vulkan/OpenGL and support ARM64/Android).
3. Replace the console-specific bits (title/achievements, content pipeline formats, XACT audio) with
   host equivalents.

This is **not** a ReXGlue recompilation and should be tracked as a separate workstream.

---

## 10. Measured vs inferred (explicit)

**Measured (from decrypted bytes / parsed structures):**
- Every asset is `XEX2`; encryption `NORMAL`, compression `NORMAL` (LZX), `game.exe.xex` BASIC.
- `game.exe.xex` embeds a managed PE (`machine 0x014C`, COR20 `ILONLY`, entry token
  `0x060008CB`, `_CorExeMain`/`mscoree.dll`, metadata `v2.0.50727`, IL tiny/fat headers present).
- The six framework `.dll.xex` embed managed PEs with COR20 `0x9` (ILONLY|STRONGNAMESIGNED).
- `default.xex`, `HostLoader.dll`, `RuntimeHost.xex`, `NetCFUserMode.dll` are native PPC
  (`machine 0x01F2`, real PPC instructions, no CLR directory); `RuntimeHost.xex` contains the CLR
  (IL metadata reader strings + reflection types) and the XNA native/shader layer.
- `default.xex` imports `HostLoader` ordinal 1 (thunk `0x820490D4`); `HostLoader` exports ordinal 1
  at `0x820B1048`, which codegen already recompiles as `sub_820B1048`.
- No `.dlx`, `#NativeImage`, `ReadyToRun`, or `NATIVE_ENTRYPOINT` anywhere.
- No ReXGlue CLI `dump`/`extract` command; guest-DLL exports are never registered in
  `ExportResolver` by codegen.

**Inferred (with basis):**
- The IL is JIT-compiled to PowerPC at runtime by the .NET CF execution engine (basis: `ILONLY`
  images + no native images + CLR present + .NET CF/JIT documented behavior).
- ReXGlue cannot execute runtime-generated PPC (basis: SDK README + `FunctionDispatcher` design,
  static address→host-function mapping, no interpreter/JIT).

**Unknown / not established here:**
- Whether the CLR is a full JIT vs. a hybrid interpreter/JIT (no `JIT` string is present in the
  binary; the "Compile/CodeGen/assembler" strings in `RuntimeHost.xex` are the D3DX **shader**
  compiler, not the CLR). Either way, the static-AOT conclusion is unchanged.
- Exact `.embsec_` section contents (statically linked native libraries; not individually mapped).
