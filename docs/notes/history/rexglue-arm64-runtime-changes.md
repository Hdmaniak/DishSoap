# ARM64 Runtime — Concrete Patch Proposals

**Status: PROPOSED — NOT APPLIED.** `<rexglue-sdk>` is owned by another agent
and is treated as read-only. Nothing here has been written to the SDK.
**Applies to:** SDK v0.10.0 (`c94f5eb`), target `aarch64-linux-android` min API 26, NDK r28b.
**Companion audit:** `../notes/arm64-runtime-audit.md`.

These patches are runtime-specific and complement the build-level set in
`patches/android-sdk-changes.md`:

- **Patch A here is the concrete implementation of `android-sdk-changes.md` Patch 11**, which was
  explicitly left as a *sketch* ("not complete — needs validation on-device"). It replaces the
  POSIX `ucontext` fiber backend with a hand-written AAPCS64 context switch.
- Patches B–D are new and were not covered there.

Diffs are `git apply`-style, relative to the SDK root.

---

## Patch A — Android/aarch64 `Fiber` backend (closes P0 fiber blocker)

### Rationale

`src/core/fiber_posix.cpp` uses `getcontext`/`makecontext`/`swapcontext`, which Bionic does not
provide (see audit §3.2). Android defines `REX_PLATFORM_LINUX=1`
(`include/rex/platform.h:34-37`), so the POSIX file is compiled and fails. Replace it on Android with
a minimal callee-saved register switch.

### A1 — `include/rex/thread/fiber.h`

Do not pull in `<ucontext.h>` on Android, and add the aarch64 save-area member.

```diff
--- a/include/rex/thread/fiber.h
+++ b/include/rex/thread/fiber.h
@@ -14,13 +14,18 @@
 #include <rex/platform.h>
 #include <cstddef>
 
-#if REX_PLATFORM_LINUX || REX_PLATFORM_MAC
+#if REX_PLATFORM_MAC || (REX_PLATFORM_LINUX && !REX_PLATFORM_ANDROID)
 #if REX_PLATFORM_MAC && !defined(_XOPEN_SOURCE)
 // Darwin hides the deprecated ucontext APIs unless _XOPEN_SOURCE is defined
 // before including <ucontext.h>.
 #define _XOPEN_SOURCE 700
 #endif
 #include <ucontext.h>
 #include <cstdint>
 #include <vector>
 #endif
+
+#if REX_PLATFORM_ANDROID
+#include <cstdint>
+#include <vector>
+#endif
@@ -56,12 +61,38 @@
 #if REX_PLATFORM_WIN32
   void* handle_ = nullptr;
   bool is_thread_fiber_ = false;
-#elif REX_PLATFORM_LINUX || REX_PLATFORM_MAC
+#elif REX_PLATFORM_ANDROID
+  // aarch64 register save area for the hand-written switch in
+  // src/core/fiber_android.cpp. Offsets are validated there with
+  // static_assert; keep both definitions in sync.
+  struct Context {
+    uint64_t x19, x20, x21, x22, x23, x24, x25, x26, x27, x28;
+    uint64_t x29, x30;                              // fp, lr
+    uint64_t d8, d9, d10, d11, d12, d13, d14, d15;  // AAPCS64 callee-saved
+    uint64_t sp;
+  };
+  Context context_{};
+  std::vector<uint8_t> stack_;
+  void (*entry_)(void*) = nullptr;
+  void* arg_ = nullptr;
+  bool is_thread_fiber_ = false;
+
+  static void Trampoline();
+#elif REX_PLATFORM_LINUX || REX_PLATFORM_MAC
   ucontext_t context_{};
   std::vector<uint8_t> stack_;
   void (*entry_)(void*) = nullptr;
   void* arg_ = nullptr;
   bool is_thread_fiber_ = false;
 
   static void Trampoline();
 #endif
 };
```

### A2 — `src/core/fiber_posix.cpp`

Compile the POSIX backend to nothing on Android (Android still defines `REX_PLATFORM_LINUX`).

```diff
--- a/src/core/fiber_posix.cpp
+++ b/src/core/fiber_posix.cpp
@@ -15,7 +15,7 @@
 #endif
 
 #include <rex/platform.h>
-#if REX_PLATFORM_LINUX || REX_PLATFORM_MAC
+#if (REX_PLATFORM_LINUX || REX_PLATFORM_MAC) && !REX_PLATFORM_ANDROID
 
 #include <rex/thread/fiber.h>
 
```

(Leave the trailing `#endif  // REX_PLATFORM_LINUX || REX_PLATFORM_MAC` comment; optionally update it.)

### A3 — new file `src/core/fiber_android.cpp`

```diff
--- /dev/null
+++ b/src/core/fiber_android.cpp
@@ -0,0 +1,137 @@
+/**
+ * @file        rex/core/fiber_android.cpp
+ * @brief       Android/aarch64 backend for rex::thread::Fiber.
+ *
+ * Bionic defines ucontext_t but implements none of
+ * getcontext/setcontext/makecontext/swapcontext, so the POSIX backend cannot
+ * be used on Android. This backend replaces it with a minimal AAPCS64
+ * cooperative context switch.
+ *
+ * Only the callee-saved registers are preserved per AAPCS64:
+ *   x19-x28, x29 (fp), x30 (lr), d8-d15, sp.
+ * The fresh context's x30 is seeded with Fiber::Trampoline, so the first
+ * switch resumes directly into the trampoline on the new stack.
+ *
+ * NOTE: unlike ucontext's swapcontext(), this switch does NOT save/restore
+ * the signal mask. The runtime's suspend/callback design is per-thread-signal
+ * based and does not rely on per-fiber masks; document/keep it that way.
+ */
+
+#include <rex/platform.h>
+
+#if REX_PLATFORM_ANDROID
+
+#include <rex/thread/fiber.h>
+
+#include <cassert>
+#include <cstddef>
+#include <cstdint>
+#include <cstdlib>
+#include <cstring>
+
+namespace rex::thread {
+
+// Must match Fiber::Context in fiber.h.
+struct Arm64FiberContext {
+  uint64_t x19, x20, x21, x22, x23, x24, x25, x26, x27, x28;
+  uint64_t x29, x30;
+  uint64_t d8, d9, d10, d11, d12, d13, d14, d15;
+  uint64_t sp;
+};
+
+static_assert(sizeof(Arm64FiberContext) == 168, "context size changed");
+static_assert(offsetof(Arm64FiberContext, x30) == 88, "x30 offset changed");
+static_assert(offsetof(Arm64FiberContext, d8) == 96, "d8 offset changed");
+static_assert(offsetof(Arm64FiberContext, sp) == 160, "sp offset changed");
+
+// void rex_arm64_swapcontext(void* save, const void* restore)
+//   x0 = save    : current fiber context (written)
+//   x1 = restore : target  fiber context (read)
+//
+// Basic asm only (no operands) is required inside a naked function.
+// Offsets are hard-coded to match Arm64FiberContext (checked above).
+extern "C" __attribute__((naked, noinline, used)) void rex_arm64_swapcontext(
+    void*, const void*) {
+  __asm__ __volatile__(
+      "stp x19, x20, [x0, #0]\n\t"
+      "stp x21, x22, [x0, #16]\n\t"
+      "stp x23, x24, [x0, #32]\n\t"
+      "stp x25, x26, [x0, #48]\n\t"
+      "stp x27, x28, [x0, #64]\n\t"
+      "stp x29, x30, [x0, #80]\n\t"
+      "stp d8,  d9,  [x0, #96]\n\t"
+      "stp d10, d11, [x0, #112]\n\t"
+      "stp d12, d13, [x0, #128]\n\t"
+      "stp d14, d15, [x0, #144]\n\t"
+      "mov x2, sp\n\t"
+      "str x2, [x0, #160]\n\t"
+      "ldp x19, x20, [x1, #0]\n\t"
+      "ldp x21, x22, [x1, #16]\n\t"
+      "ldp x23, x24, [x1, #32]\n\t"
+      "ldp x25, x26, [x1, #48]\n\t"
+      "ldp x27, x28, [x1, #64]\n\t"
+      "ldp x29, x30, [x1, #80]\n\t"
+      "ldp d8,  d9,  [x1, #96]\n\t"
+      "ldp d10, d11, [x1, #112]\n\t"
+      "ldp d12, d13, [x1, #128]\n\t"
+      "ldp d14, d15, [x1, #144]\n\t"
+      "ldr x2, [x1, #160]\n\t"
+      "mov sp, x2\n\t"
+      "ret\n\t");
+}
+
+thread_local Fiber* Fiber::tls_current_ = nullptr;
+
+Fiber* Fiber::ConvertCurrentThread() {
+  auto* f = new Fiber();
+  f->is_thread_fiber_ = true;
+  tls_current_ = f;
+  return f;
+}
+
+Fiber* Fiber::Create(size_t stack_size, void (*entry)(void*), void* arg) {
+  auto* f = new Fiber();
+  f->entry_ = entry;
+  f->arg_ = arg;
+  f->stack_.resize(stack_size);
+
+  // AAPCS64 requires sp to be 16-byte aligned at all times.
+  uintptr_t top = reinterpret_cast<uintptr_t>(f->stack_.data() + f->stack_.size());
+  top &= ~uintptr_t(0xF);
+
+  auto* ctx = reinterpret_cast<Arm64FiberContext*>(&f->context_);
+  std::memset(ctx, 0, sizeof(*ctx));
+  ctx->sp = top;
+  // First switch "returns" (ret) into the trampoline.
+  ctx->x30 = reinterpret_cast<uint64_t>(&Fiber::Trampoline);
+  return f;
+}
+
+void Fiber::Trampoline() {
+  Fiber* f = tls_current_;
+  f->entry_(f->arg_);
+  // Guest fibers do not return (FiberEntryPoint switches back or terminates);
+  // a plain return would pop a garbage LR, so fail loudly.
+  std::abort();
+}
+
+void Fiber::SwitchTo(Fiber* target) {
+  Fiber* from = tls_current_;
+  tls_current_ = target;
+  // Keep the TLS store ordered w.r.t. the stack switch.
+  __asm__ __volatile__("" ::: "memory");
+  rex_arm64_swapcontext(&from->context_, &target->context_);
+}
+
+void Fiber::Destroy() {
+  if (is_thread_fiber_) {
+    tls_current_ = nullptr;
+  } else {
+    assert(this != tls_current_ && "Destroy called on the currently running fiber");
+  }
+  delete this;
+}
+
+}  // namespace rex::thread
+
+#endif  // REX_PLATFORM_ANDROID
```

### A4 — `src/core/CMakeLists.txt`

`fiber_android.cpp` is fully `#if REX_PLATFORM_ANDROID`-guarded, so it can simply be added to the
POSIX source list; on desktop it compiles to an empty TU.

```diff
--- a/src/core/CMakeLists.txt
+++ b/src/core/CMakeLists.txt
@@ -63,6 +63,7 @@ elseif(UNIX)
         dynlib_posix.cpp
         exception_handler_posix.cpp
         filesystem_posix.cpp
+        fiber_android.cpp
         fiber_posix.cpp
         mapped_memory_posix.cpp
         math_gcc.cpp
```

### A5 — verification (no heavy build needed)

```bash
NDK=$HOME/android-ndk-r28b
CLANG=$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/clang++
# 1. The TU compiles for the Android target.
$CLANG --target=aarch64-linux-android26 -std=c++23 -Iinclude -Ithirdparty/simde \
       -c src/core/fiber_android.cpp -o /tmp/fiber_android.o
# 2. The switch is emitted with no prologue and the expected instruction mix.
$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-objdump -d /tmp/fiber_android.o \
       | sed -n '/rex_arm64_swapcontext/,/ret/p'
#    expect: stp x19..x28, stp x29/x30, stp d8..d15, mov x2,sp, str; then ldp..., ldr, mov sp, ret
```

On-device: create/switch/destroy a fiber and confirm `SP & 0xF == 0` at the trampoline entry (add a
temporary assert), and that a workload using `CreateFiber`/`SwitchToFiber` round-trips.

> If a `.S` file is preferred over a naked function (e.g. to avoid clang-version quirks), the same
> instruction sequence can live in `src/core/fiber_android_switch.S` with `.globl rex_arm64_swapcontext`;
> add it to the CMake list with the `ASM` language enabled. The naked-function form above avoids
> touching the project's ASM language settings.

---

## Patch B — guard x86-only test compile flags (P1)

`tests/ppc/CMakeLists.txt:99` applies `-msse4.1 -mssse3` unconditionally, which breaks any aarch64
build of the host test suite (cross builds, CI on ARM).

```diff
--- a/tests/ppc/CMakeLists.txt
+++ b/tests/ppc/CMakeLists.txt
@@ -96,8 +96,10 @@
 target_compile_options(ppc_tests PRIVATE
     -UTRACY_ENABLE -UREXGLUE_ENABLE_PROFILING
     -UREXGLUE_ENABLE_PERF_COUNTERS -UREXGLUE_PROFILE_GUEST_FUNCTIONS
-    -msse4.1 -mssse3
 )
+if(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64")
+    target_compile_options(ppc_tests PRIVATE -msse4.1 -mssse3)
+endif()
 
 include(Catch)
```

---

## Patch C — Android W^X hardening (P1, defensive)

No live caller requests executable memory (audit §4), so this cannot break normal operation, but it
prevents an accidental `mprotect(PROT_EXEC)`/`mmap(RWX)` from returning `EACCES` on Android and
makes the capability query honest.

### C1 — `src/core/memory_posix.cpp`

```diff
--- a/src/core/memory_posix.cpp
+++ b/src/core/memory_posix.cpp
@@ -139,9 +139,13 @@
 bool IsWritableExecutableMemorySupported() {
-#if REX_PLATFORM_MAC
+#if REX_PLATFORM_MAC || REX_PLATFORM_ANDROID
   // macOS enforces W^X on Apple Silicon. Shared file mappings cannot be both
   // writable and executable. The code cache must use separate RW and RX views.
+  // Android likewise enforces W^X via SELinux (execmem); anonymous
+  // PROT_WRITE|PROT_EXEC mappings are denied for apps. This runtime does not
+  // need them: guest code is recompiled to host code at build time, so no
+  // executable guest memory is ever requested.
   return false;
 #else
   return true;
 #endif
 }
```

### C2 — `src/core/memory.cpp`

```diff
--- a/src/core/memory.cpp
+++ b/src/core/memory.cpp
@@ -26,7 +26,11 @@
 namespace memory {
 
 bool IsWritableExecutableMemoryPreferred() {
+#if REX_PLATFORM_ANDROID
+  return false;
+#else
   return REXCVAR_GET(writable_executable_memory);
+#endif
 }
```

Optionally also make `AllocFixed` (`memory_posix.cpp:240`) reject `PageAccess::kExecuteReadWrite`
with a clear log on Android instead of silently returning `nullptr` after `EPERM`. This is not
required for The Dishwasher because no such allocation is made.

---

## Patch D — `m128_*` helpers on ARM64 (P1 hygiene)

`include/rex/math.h:193-228` defines `m128_f32/i32/f64/i64` only under `REX_ARCH_AMD64` and has no
callers today (audit §1.3). Two options:

**D1 — delete the dead block** (smallest change; if nothing calls it, remove it):

```diff
--- a/include/rex/math.h
+++ b/include/rex/math.h
@@ -190,40 +190,6 @@
 template <typename T>
 inline T rotate_left(T v, uint8_t sh) {
   return (T(v) << sh) | (T(v) >> ((sizeof(T) * 8) - sh));
 }
 
-#if REX_ARCH_AMD64
-// Utilities for SSE values.
-template <int N>
-float m128_f32(const __m128& v) {
-  float ret;
-  _mm_store_ss(&ret, _mm_shuffle_ps(v, v, _MM_SHUFFLE(N, N, N, N)));
-  return ret;
-}
-... (i32 / f64 / i64 overloads) ...
-#endif
-
 // Similar to the C++ implementation of XMConvertFloatToHalf ...
```

**D2 — keep it and add an ARM64 body** (preferred if future runtime code may want it). Because
`simde__m128` is the portable type on aarch64, the signatures differ:

```cpp
#elif REX_ARCH_ARM64
#include <simde/x86/sse.h>
#include <bit>
namespace rex {
template <int N>
inline float m128_f32(simde__m128 v) {
  alignas(16) float lanes[4];
  simde_mm_storeu_ps(lanes, v);
  return lanes[N];
}
template <int N>
inline int32_t m128_i32(simde__m128 v) {
  return std::bit_cast<int32_t>(m128_f32<N>(v));
}
// ... mirror f64/i64 using simde_mm_storeu_pd on simde__m128d ...
}
#endif
```

Do **not** reintroduce `<xmmintrin.h>` on ARM64.

---

## Patch ordering / relationship to the other patch set

1. Apply `patches/android-sdk-changes.md` Patches 01–10 first (configure/compile/link/entry).
2. Apply **Patch A** here in place of `android-sdk-changes.md` Patch 11 (it is the completed form of
   that sketch). It is required for guest threads and the guest `CreateFiber`/`SwitchToFiber` API to
   run at all.
3. Patch B is independent (test tooling). Patches C/D are hardening/hygiene and safe to defer, but C
   is recommended before shipping on Android.

**Nothing in this file has been applied to the SDK tree.**
