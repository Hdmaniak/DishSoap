# ARM64 Runtime & Fiber Audit — ReXGlue SDK v0.10.0

**Role:** ARM64 & Fiber Runtime Specialist (subagent).
**Scope:** read-only audit of `<rexglue-sdk>` (git `c94f5eb "Release v0.10.0"`),
focused on the **runtime** and **SDK**, not on not-yet-generated game code.
**Target:** `aarch64-linux-android` (Bionic libc), min API 26, NDK r28b (`28.1.13356709`, clang 19),
C++23/C17, `-march=armv8-a+simd`.
**Method:** static source audit + NDK sysroot/stub verification. **No build was run** (the SDK tree is
owned by another agent and is read-only). Every claim cites `file:line`.

Companion docs: `notes/android-support-audit.md` (whole-SDK readiness) and
`patches/android-sdk-changes.md` (build-level patch set, incl. a *sketch* of the fiber backend).
This document adds the runtime-specific depth: x86-ism categorisation, the VMX/NEON code path,
the concrete aarch64 fiber switch, stack-alignment rules, and W^X.

---

## 0. Verdicts at a glance

| Area | Verdict | One-line summary |
|---|---|---|
| x86-isms (task 1) | **GREEN, 1 trap** | 117 `_mm*` / 83 `__m128*` tokens exist but **all are inside `REX_ARCH_AMD64` branches or AMD64-only helpers**; 0 `__builtin_ia32`, 0 x86 asm clobbers. SIMDe covers the codegen path. Only trap: dead AMD64-only `m128_*` helpers in `math.h`. |
| VMX / AltiVec (task 2) | **GREEN (SIMDe → NEON)** | Guest VRs are a scalar 16-byte union; all VMX ops are emitted as `simde_mm_*` and lower to NEON on aarch64. A few PPC-specific ops round-trip through memory. |
| Fibers / context switch (task 3) | **RED** | Bionic has **no** `getcontext`/`makecontext`/`swapcontext` → build + link + runtime blocker. Needs an aarch64 assembly switch. 16-byte SP alignment is mandatory. |
| W^X / JIT (task 4) | **GREEN** | No runtime CPU codegen. `rexcodegen` is a build-time static lib; guest code is host C++. `PROT_EXEC` paths are latent/dead. Android W^X will not block emulation. |
| `pthread_getattr_np` | **GREEN** | Present in Bionic (`pthread.h:169`); not currently used. |
| `pthread_getname_np` | **GREEN** | API 26, already `dlopen`'d (`threading_posix.cpp:67-85`). |
| RT signal handlers | **GREEN (low residual)** | `SIGRTMIN+n` usable on Bionic (bionic's own `signal_test.cpp` registers `SIGRTMIN`). Verify on device. |

---

## 1. x86-ism audit

### 1.1 Counts by category (word-boundary matches in `src/` + `include/`)

| Category | Hits | Notes |
|---|---:|---|
| SSE/AVX intrinsic **tokens** (`_mm*_`, bare `\b_mm…`) | **117** | 115 live + **2 are comments** (`src/codegen/builders/vector.cpp:1098,1127`). |
| 128/256-bit vector **types** (`__m128*`, `__m256*`) | **83** | all in AMD64-guarded regions. |
| x86 SIMD **headers** (`<xmmintrin.h>`, `<emmintrin.h>`, `<immintrin.h>`, `<tmmintrin.h>`) | **3** | every include is under `#if REX_ARCH_AMD64`. |
| `__builtin_ia32_*` | **0** | — |
| `__x86_64__` / `_M_X64` conditionals | **5** | `intrinsics.h:239,290,321` (with aarch64 arms), `platform.h:114` (debugtrap), `fpscr.h:28` (FPCR). |
| inline asm containing x86 (`int3`) | **1** | `include/rex/platform.h:115`, under `#if defined(__x86_64__) \|\| defined(__i386__)`, with a `__builtin_trap()` `#else`. |
| x86 register clobbers in asm (`"xmm"`, `"=x"`) | **0** | — |
| `-mssse3` / `-mavx` / `-mavx2` (repo-wide, excl. thirdparty) | **0** | — |
| `-msse4.1` | **2** | `cmake/rexglue_helpers.cmake:60` (guarded by `CMAKE_SYSTEM_PROCESSOR MATCHES x86_64`) and `tests/ppc/CMakeLists.txt:99` (**unguarded**, host-only test target). |
| MSVC intrinsics (`<intrin.h>`, `_BitScan*`) | ms | `src/core/math_msvc.cpp`, `src/core/atomic_win.cpp`, `include/rex/o1heap_config.h` — all `_MSC_VER`-guarded. |

Per-file distribution of the 117 `_mm*` tokens and 83 `__m128*` types:

| File | `_mm*` | `__m128*` | Guard |
|---|---:|---:|---|
| `include/rex/audio/conversion.h` | 36 | 23 | `#if REX_ARCH_AMD64` (`:24`) … `#else` (`:112`) scalar fallback |
| `src/core/memory.cpp` | 30 | 30 | `#if REX_ARCH_AMD64` (`:49`) … `#elif REX_ARCH_ARM64` (`:208`) NEON `vqtbl1q_u8` |
| `src/audio/xma_context.cpp` | 21 | 15 | `#if REX_ARCH_AMD64` (`:751`) … `#else` (`:800`) scalar |
| `src/graphics/primitive_processor.cpp` | 12 | 2 | `#elif REX_ARCH_ARM64` branches (`:959,1019,1034,1086,1130,1179`) |
| `include/rex/graphics/primitive_processor.h` | 10 | 7 | `#if REX_ARCH_AMD64` … `#elif REX_ARCH_ARM64` (`<arm_neon.h>`, `:40-41`) |
| `include/rex/math.h` | 6 | 6 | `#if REX_ARCH_AMD64` (`:193-228`), **dead** (see 1.3) |
| `src/codegen/builders/vector.cpp` | 2¹ | 0 | ¹comments only; emits `simde_mm_*` |

### 1.2 SIMDe coverage

SIMDe is vendored (`thirdparty/simde`, submodule) and exposed as a header-only include dir
(`thirdparty/CMakeLists.txt:38,98-103`). Runtime headers that already use it:

- `include/rex/ppc/context.h:24-26` — `simde/x86/{avx,sse,sse4.1}.h`
- `include/rex/ppc/intrinsics.h:19-23` — `simde/x86/{avx,avx2,sse,sse4.1}.h`
- `include/rex/platform/fpscr.h:17` — `simde/x86/sse.h`
- `src/codegen/builders/vector.cpp:17`, `context.cpp`, `fpscr.h`

SIMDe auto-detects aarch64 and lowers to **native NEON** (no `SIMDE_NO_NATIVE` / SIMDE_ALWAYS
defines anywhere in `src/`+`include/`). Coverage of the intrinsic families actually referenced:
`punpck*`, `pack*`, `shuffle_epi8`, `alignr`, `cvtepi*`, `cvttps_epi32`, `dp_ps`, `blend*`,
`permute*`, `slli/srli/srai`, `min/max`, `and/andnot/or/xor`, `movemask`, `cmpeq/cmpgt`, `mul/add/sub/div`, `sqrt`,
`round_ps` — all in `simde/x86/sse.h`, `sse2.h`, `ssse3.h`, `sse4.1.h`, `avx.h`, `avx2.h` (and `sse3.h`).

### 1.3 The one real trap

`include/rex/math.h:193-228` (`m128_f32/i32/f64/i64`) is compiled **only** on AMD64 and has **no ARM64
equivalent**. A repo-wide grep for call sites returns nothing outside `math.h`, so it is currently
dead code and harmless — **but** any future runtime or generated code that calls `m128_f32<…>` will
fail to compile on aarch64. Either delete it or add a SIMDe/`std::bit_cast`-based ARM64 body.

### 1.4 Everything else is correctly guarded

No unguarded `_mm*`/`__m128*` token reaches an aarch64 translation unit. The `#else` branches in
`conversion.h` / `xma_context.cpp` are scalar and correct; `memory.cpp` has a proper hand-written
NEON path; `primitive_processor` has full AMD64/ARM64 parity. The only build-system issue is the
unguarded `-msse4.1 -mssse3` on the **host** `ppc_tests` target (`tests/ppc/CMakeLists.txt:99`),
which is irrelevant to the Android target but should still be arch-guarded for cross builds.

---

## 2. PPC VMX / AltiVec emulation path

### 2.1 Representation of the 128-bit guest vector registers

- **Scalar union, not a native vector type.** `include/rex/ppc/context.h:123-135`:
  ```cpp
  union alignas(0x10) VRegister {
    int8_t s8[16]; uint8_t u8[16]; int16_t s16[8]; uint16_t u16[8];
    int32_t s32[4]; uint32_t u32[4]; int64_t s64[2]; uint64_t u64[2];
    float f32[4]; double f64[2];
  };
  ```
- `PPCContext` (extended VMX128) stores `v0..v127` of this type (`include/rex/ppc/context.h`, used at
  `src/system/xthread.cpp:1063,1116-1123`). The guest context is **memory-backed**; the codegen
  addresses `vN`/`ctx.vN` via `BuilderContext::v()` (`src/codegen/builders/builder_context.h:132-138`).
- There is no `__vector`/Altivec type and no `vec_*` intrinsic in the runtime. `docs/ppc/vmx128.txt`
  is the guest ISA reference only.

### 2.2 Codegen emits SIMDe, which lowers to NEON

The builder emits `simde_mm_*` calls that load from / store to `.f32`/`.u8` views of the union:

- helpers: `src/codegen/builders/context.cpp:426-476` (`emit_vec_fp_binary`, `emit_vec_fp_unary_expr`,
  `emit_vec_int_binary`, `emit_vec_int_binary_swapped`, `emit_vec_var_shift`).
- ops: `src/codegen/builders/vector.cpp` (e.g. `:191` `dp_ps`, `:265-284` saturating add,
  `:500-546` logical, `:616-720` compares, `:1088-1165` packs, `:1530-1557` extends).
- ARM64-specific intrinsics in `include/rex/ppc/intrinsics.h`:
  - `simde_mm_vsl` — `:239-262` (`__aarch64__` NEON `vshlq_u64` path)
  - `simde_mm_vslo` — `:290-305` (memory `memcpy`)
  - `simde_mm_vsro` — `:321-336` (memory `memcpy`)
  - plus custom `simde_mm_perm_epi8_`, `simde_mm_sllv/srlv/srav`, `simde_mm_avg_*`,
    `simde_mm_cmpgt_epu*`, `simde_mm_cvtepu32_ps_`, `simde_mm_vctsxs/vctuxs`, `simde_mm_adds_epu32`.

### 2.3 Verdict / performance

**The ARM64 VMX path is SIMDe-based and lands on NEON** — not scalar. However, it is **not
register-resident**: every PPC vector op is emitted as
`simde_mm_store_ps(dst.f32, simde_mm_op(simde_mm_load_ps(src.f32), …))`, i.e. load-modify-store on
a 16-byte union in `PPCContext`, because the codegen models the guest VR file as memory (matching
Xenia's original design). Consequences for performance-critical 2D math (Dishwasher sprite/particle
transforms are mostly `vmaddfp`/`vmulfp128`/`vperm`/`vpkd3d128`):

- The arithmetic itself runs on NEON (128-bit, full throughput). **Good.**
- A handful of PPC-exact ops (`vperm`/`vsel`/`vsl`/`vslo`/`vsro`/`vpkd3d128`) use **memory round-trips**
  or per-element scalar loops inside the SIMDe helper (`intrinsics.h:290-336`, `perm_epi8_`). **Slow
  relative to native** but correctness-preserving.
- Because each emitted op reloads/stores `ctx.vN`, the compiler cannot keep vectors in NEON registers
  across a basic block. This is a throughput tax, not a correctness issue.

**Recommendation:** correctness-first — ship the SIMDe path. Only if 2D math proves to be a hotspot
should the codegen be taught to keep a small set of VRs in `simde__m128` locals within a block (a
codegen change, out of scope for this runtime audit). Do **not** rewrite the VR file to
`__m128`/NEON directly: the memory layout (`alignas(16)` union with the `u8`/`f32` overlapping views
and `vec128_t` conversions) is load-bearing for the rest of the runtime and for Xenia-identical
semantics.

---

## 3. Fiber / context-switch audit

### 3.1 Current mechanism (desktop)

`include/rex/thread/fiber.h` + `src/core/fiber_posix.cpp` implement the host fiber primitive with
**`ucontext` (makecontext/swapcontext)**:

- `fiber.h:17-26` includes `<ucontext.h>` when `REX_PLATFORM_LINUX || REX_PLATFORM_MAC`.
- `fiber.h:58-61` stores `ucontext_t context_{}; std::vector<uint8_t> stack_;`.
- `fiber_posix.cpp:30-36` `ConvertCurrentThread()` → `getcontext`.
- `fiber_posix.cpp:41-57` `Create()` → `getcontext` + set `uc_stack` + `makecontext`.
- `fiber_posix.cpp:65-69` `SwitchTo()` → `swapcontext`.

### 3.2 Bionic availability — HARD BLOCKER

Android defines **both** `REX_PLATFORM_ANDROID` and `REX_PLATFORM_LINUX`
(`include/rex/platform.h:34-37`), so `fiber_posix.cpp`'s `#if REX_PLATFORM_LINUX || REX_PLATFORM_MAC`
(`:16`) **is true on Android** and the file is compiled (`src/core/CMakeLists.txt:66`).

Verified against the actual NDK r28b sysroot:

```
$SYS/usr/include/sys/ucontext.h   # defines ucontext_t / mcontext_t / gregset_t ONLY
$ grep -rln -E '\b(getcontext|setcontext|makecontext|swapcontext)\s*\(' $SYS/usr/include
  (no matches)
$ readelf -sW .../aarch64-linux-android/21/libc.so | grep -iE 'context'
  (no matches)
```

**Conclusion:** Bionic exposes `ucontext_t` but declares/exports **none** of
`getcontext`/`setcontext`/`makecontext`/`swapcontext`. `fiber_posix.cpp` cannot compile or link on
Android as shipped. This is P0 and is correctly flagged as such in
`notes/android-support-audit.md` (row "Fibers").

### 3.3 Who depends on it (so it must actually work)

- Guest-thread scheduler: `src/system/xthread.cpp:641-644` converts the host thread to a fiber
  (`Fiber::ConvertCurrentThread`), then `Execute()` runs the guest entry.
- Win32/CRT fiber API shims: `src/kernel/crt/threading.cpp:162` (`ConvertCurrentThread`),
  `:243` (`Fiber::Create(host_stack, …)`), `:328` (`Fiber::SwitchTo`).
- Guest fibers validated: `CreateFiber_entry` allocates a host stack
  `max(guest_stack_size, 256 KiB)` (`crt/threading.cpp:236-243`).

So replacing ucontext is mandatory for **scheduler correctness**, not just the build.

### 3.4 Stack alignment (aarch64 / AAPCS64)

- **AAPCS64 requires `sp` to be 16-byte aligned at all times** (at every public interface and at
  every load/store of a 128-bit value). A hand-rolled switch **must** set
  `sp = (stack_top & ~15)` and preserve it into the trampoline.
- The backing store `std::vector<uint8_t> stack_` is aligned to `alignof(max_align_t)` (= 16 on
  aarch64), so the raw buffer base is 16-aligned, but the **top** must still be rounded down.
- `makecontext` (glibc) historically aligned the stack; that behaviour must be replicated manually.
- The context save set must be the **callee-saved** registers per AAPCS64: `x19–x28`,
  `x29`(fp), `x30`(lr), and `d8–d15` (v8–v15 low halves), plus `sp`. Caller-saved `x0–x18`,
  `v0–v7`, `v16–v31` need not be saved. **`x30` must be seeded with the trampoline address** and the
  trampoline must be `noreturn` (see patch).
- Guest *PPC* stack alignment is a separate concern and is already handled: guest kernel stacks are
  page-aligned and the CRT fiber path uses `initial_sp = stack_top - 0x50`
  (`crt/threading.cpp:214-217`); the guest-side non-volatile save/restore is done via
  `X_FIBER_CONTEXT` (`crt/threading.cpp:300-336`), independent of the host switch.

### 3.5 glibc-vs-Bionic assumptions around threading

| Assumption | Where | Status on Bionic |
|---|---|---|
| `getcontext`/`makecontext`/`swapcontext` | `fiber_posix.cpp:31,55,68`; `fiber.h:23` | **ABSENT** → P0. |
| `pthread_getattr_np` | not used anywhere | **Available** (`sysroot .../pthread.h:169`). |
| `pthread_getname_np` | `threading_posix.cpp:78` | API 26; guarded `dlopen` (`:67-92`) — OK. |
| `pthread_cancel` async | `threading_posix.cpp:108-113,1401-1404` | Bypassed on Android via a signal handler. OK. |
| `SIGRTMIN+n` handlers | `threading_posix.cpp:117-134` | Bionic usable; `SIGRTMIN` is a function call. Verify on device. |
| thread stack size | `thread.h:397` (4 MiB default), `xthread.cpp:433` (16 MiB) | `pthread_attr_setstacksize` (`threading_posix.cpp:654`) — OK. |
| signal masks across switch | `SwapContext` implicitly | With a custom switch, the signal mask is **not** swapped (unlike `swapcontext`). The runtime's suspend/callback design uses per-thread signals and `alertable_state_`, so this is acceptable but must be documented. |
| `_FORTIFY_SOURCE` / TLS | n/a in runtime | No `__libc_stack_end`, no `__thread`-based switch assumptions. TLS (`thread_local Fiber::tls_current_`, `crt/threading.cpp`) is per-thread and unaffected by a same-thread stack switch. |

### 3.6 Verdict

**RED — must replace `ucontext` with an aarch64 context switch.** The concrete implementation
(shapes and register set) is in `patches/arm64-runtime-changes.md` (Patch A). Notes:
- Keep the public `Fiber` interface unchanged so `xthread.cpp` and `crt/threading.cpp` need no edits.
- `tls_current_` update before the switch (as today) works because both fibers run on the same thread.
- Use a `DMB`/`ISB`-free plan is fine for same-thread cooperative switching; a compiler barrier
  (`"memory"` clobber) around the switch is still recommended.
- The signal mask is intentionally not swapped; document this divergence from `swapcontext`.

---

## 4. W^X / JIT-adjacent audit

### 4.1 There is no runtime CPU code generator

- `src/codegen/CMakeLists.txt:1-4` — "Codegen library - PPC to C++ recompiler"; it builds
  `add_library(rexcodegen STATIC …)` (`:83`). It is a **build-time** static library; the runtime
  consumes pre-generated `.cpp` (via `resources/templates/codegen/*`). No runtime `jit`/`LLVM`/
  `asmjit`/`xbyak`/`dynasm` exists (grep clean; the only `Assembler` hits are DXBC shader assemblers).
- `src/system/elf_module.cpp:148-150` states it outright:
  > `// crack: No JIT backend to notify about executable code`
  > `// In JIT mode this would be: processor_->backend()->CommitExecutableRange(...)`
- The function dispatch table (`src/system/xmemory.cpp:791-846`) stores **host function pointers**
  (`PPCFunc*`) in guest-mapped RW memory — data, not code.

### 4.2 Executable-memory paths exist but are dead

- `src/core/memory_posix.cpp:123-137` maps `PageAccess::kExecuteReadWrite` →
  `PROT_READ | PROT_WRITE | PROT_EXEC` and `kExecuteReadOnly` → `PROT_READ | PROT_EXEC`.
  `AllocFixed` (`:240-331`) and `Protect` (`:340-…`) call `mprotect`/`mmap` with those flags.
- **No live caller requests execute access.** A repo-wide grep for `kExecuteReadOnly` /
  `kExecuteReadWrite` finds only:
  - the definition/translation in `src/core/memory_posix.cpp` and `memory_win.cpp`;
  - `src/system/xmemory.cpp:891-899` `FromPageAccess()`, which **`assert_always()`s** with the comment
    *"Guest memory cannot be executable - this should never happen :)"*.
- `IsWritableExecutableMemorySupported()` / `IsWritableExecutableMemoryPreferred()` have **no call
  sites** (`include/rex/memory/utils.h:88,93`; `src/core/memory.cpp:29-31`;
  `src/core/memory_posix.cpp:139-150`). The `writable_executable_memory` cvar (`memory.cpp:16`,
  default `true`) is therefore currently inert.
- Guest XEX/ELF module loaders explicitly map read+write, never execute:
  `xex_module.cpp:297-302,503-507`, `elf_module.cpp:135-142`, `xboxkrnl_memory.cpp:160-167`
  (guest protections are R/W/no-access only — `include/rex/system/xmemory.h:83-88` has no execute bit).

### 4.3 Verdict

**GREEN.** No executable memory is needed or requested at runtime. Android's W^X / SELinux
`execmem` policy will **not** block The Dishwasher. Two hardening notes:
1. The latent `PROT_EXEC` code paths in `memory_posix.cpp` would return `EACCES`/`EPERM` on Android
   if ever reached. Make `IsWritableExecutableMemorySupported()` return `false` on Android and/or
   have `AllocFixed` fall back to `PROT_READ|PROT_WRITE` (or fail loudly) — see Patch C.
2. The DXBC/shader translators produce GPU bytecode (Vulkan SPIR-V / D3D12 DXBC) and do not need
   `PROT_EXEC`; they are unaffected.

---

## 5. Prioritized fix list

Severity: **P0** blocker, **P1** correctness/portability, **P2** hygiene/performance.

| # | Sev | File:line | Issue | Proposed fix |
|---|---|---|---|---|
| 1 | **P0** | `src/core/fiber_posix.cpp:31,55,68`; `include/rex/thread/fiber.h:17-26,58-61`; `src/core/CMakeLists.txt:66` | ucontext API absent on Bionic → compile/link/runtime failure; guest threads + guest fibers cannot run. | Add an aarch64 assembly `Fiber` backend (`src/core/fiber_android.cpp`) and exclude `fiber_posix.cpp` on Android. Concrete patch: `patches/arm64-runtime-changes.md` **Patch A**. |
| 2 | **P1** | `include/rex/math.h:193-228` | `m128_f32/i32/f64/i64` exist only under `REX_ARCH_AMD64`; dead now, compile error for any future ARM64 caller. | Delete, or add an aarch64 body (`_mm_shuffle_ps` is available via SIMDe; or `std::bit_cast` on the union). Patch D. |
| 3 | **P1** | `tests/ppc/CMakeLists.txt:99` | `-msse4.1 -mssse3` applied unconditionally → breaks any aarch64/ARM cross build of tests. | Wrap in `if(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64")`. Patch B. |
| 4 | **P1** | `src/core/memory_posix.cpp:131-132`, `:139-150` | Latent `PROT_EXEC` translation; no Android W^X guard; `IsWritableExecutableMemorySupported()` reports `true` on Android. | On Android, return `false` from `IsWritableExecutableMemorySupported()` and refuse/downgrade `kExecute*` in `AllocFixed`/`Protect`. Patch C. |
| 5 | **P2** | `include/rex/ppc/intrinsics.h:290-336` (`vslo`/`vsro`), `perm_epi8_` | PPC vector ops round-trip through memory instead of staying in NEON registers. | Perf-only; leave unless profiling shows a hotspot. Documented in §2.3. |
| 6 | **P2** | `src/core/threading_posix.cpp:117-134` | Handlers installed on `SIGRTMIN+0/1/2`; Bionic's `SIGRTMIN` is a runtime function and the lowest RT signals are shared with the platform. | Low risk (bionic's own tests use `SIGRTMIN`), but validate on a physical device; consider `SIGRTMIN+8…` or `SIGUSR1/2` fallback if a collision is observed. |
| 7 | **P2** | `src/core/fiber_posix.cpp` (design) | A custom switch does **not** swap the signal mask (unlike `swapcontext`). | Intended, but document it and ensure suspend/callback signals are blocked appropriately around the switch. |
| 8 | **P2** | `CMakeLists.txt:161-169` (Android falls into `linux-arm64`) | Cosmetic: no `android-arm64` platform name. | Covered by `patches/android-sdk-changes.md`; not a runtime issue. |

Legend: patches A–D are in `patches/arm64-runtime-changes.md`. Patches there are **proposals only and
have not been applied** to the SDK tree.

---

## 6. Appendix — raw evidence commands

```bash
# x86 intrinsic tokens / types (word-boundary), src+include only
grep -rn -E '\b_mm(256|512)?_[a-zA-Z0-9_]+' src include | wc -l          # 117
grep -rn -E '\b__(m128|m256|m512)[a-z0-9]*\b' src include | wc -l        # 83
grep -rn -E '__builtin_ia32_' src include | wc -l                       # 0

# Bionic ucontext absence (NDK r28b)
grep -rln -E '\b(getcontext|setcontext|makecontext|swapcontext)\s*\(' \
     $NDK/toolchains/llvm/prebuilt/linux-x86_64/sysroot/usr/include   # (none)
readelf -sW $NDK/toolchains/llvm/prebuilt/linux-x86_64/sysroot/usr/lib/aarch64-linux-android/21/libc.so \
     | grep -iE 'context'                                             # (none)

# pthread_getattr_np availability
grep -n 'pthread_getattr_np' .../sysroot/usr/include/pthread.h        # :169

# no runtime JIT
grep -rniE 'asmjit|xbyak|dynasm|runtime.{0,10}codegen' src include     # (none)
sed -n '148,150p' src/system/elf_module.cpp                            # "No JIT backend"
```
