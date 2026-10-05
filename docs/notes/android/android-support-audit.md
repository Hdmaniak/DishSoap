# ReXGlue SDK v0.10.0 — Android / aarch64-linux-android readiness audit

Scope: read-only audit of `<rexglue-sdk>` (SDK 0.10.0, git `c94f5eb "Release v0.10.0"`).
Target: `aarch64-linux-android`, min API 26, NDK r28b (`28.1.13356709`, clang 19.0.0), C++23 / C17.
No build was run (another agent owns the tree). All findings cite `file:line`.

Legend for "Ready?":
- **YES** — Android-aware code exists and is self-consistent.
- **PARTIAL** — Android branches exist but are incomplete or reference missing pieces.
- **NO** — desktop-only or absent; will not build/run on Android as-is.
- **N/A** — handled outside the SDK (toolchain/packaging).

---

## 1. Support matrix

| Subsystem | Ready? | Evidence | Gap |
|---|---|---|---|
| Platform detection | **YES** | `include/rex/platform.h:35-40` maps `__ANDROID__` → `REX_PLATFORM_ANDROID` **and** `REX_PLATFORM_LINUX`; `include/rex/platform.h:79-80` maps `__aarch64__` → `REX_ARCH_ARM64` | Android is deliberately a Linux sibling (`REX_PLATFORM_GNU_LINUX` stays 0). Desktop-only code is supposed to key off `REX_PLATFORM_GNU_LINUX`, not plain `UNIX`. |
| Root CMake platform selection | **PARTIAL** | `CMakeLists.txt:161-169` — Android falls into `elseif(UNIX AND NOT APPLE)`; `REX_PLATFORM` becomes `linux-arm64`, `REX_PLATFORM_LINUX=1`. `CMakeLists.txt:180` fatal-error path is unreachable for Android. | No `ANDROID` branch: output dir is `out/linux-arm64`, no `android-arm64` name. Minor (does not block), but should be explicit. |
| Compiler gate | **YES** | `CMakeLists.txt:83` requires Clang; `:88` requires Clang ≥18. NDK r28b ships clang 19.0.0. `:98` requires 64-bit (aarch64 is 64-bit). | None. `-mcmodel=large` is AMD64-only (`:123`), aarch64 takes `-mcmodel=small` (`:125`). |
| Core POSIX threading | **PARTIAL** | Android branches: `src/core/threading_posix.cpp:45-50,67-92` (dlopen `libc.so` for `pthread_getname_np`), `:108-113` (`pthread_cancel` replaced by signal), `:709-757` (pre-API-26 name cache), `:777-785,810-818` (`sched_*affinity` instead of `pthread_*affinity_np`), `:878-883` (`sigqueue` by TID), `:986-994` (terminate via signal). | Includes missing header `<rex/main_android.h>` (`:48`); calls undefined `rex::GetAndroidApiLevel()` (`:74`) and undefined `pthread_gettid_np()` (`:778,811,879`). Does not compile as shipped. |
| Core memory / mapped memory | **PARTIAL** | Android ashmem path: `src/core/memory_posix.cpp:48-58` (`<linux/ashmem.h>` — header **is** present in NDK r28b), `:87-112` (`ASharedMemory_create` via dlopen), `:434-456` (`ASharedMemory`/`/dev/ashmem`); `src/core/mapped_memory_posix.cpp:124-143` (`OpenForAndroidContentUri`). `off64_t`/`fseeko64`/`ftello64`/`ftruncate64`/`stat64`/`fstat64`/`nftw` all exist in Bionic API ≥24. | `src/core/memory_posix.cpp:94` calls undefined `rex::GetAndroidApiLevel()`. `src/core/mapped_memory_posix.cpp:137` calls undefined `rex::filesystem::OpenAndroidContentFileDescriptor`. |
| Fibers (`getcontext`/`makecontext`/`swapcontext`) | **NO** | `src/core/fiber_posix.cpp:32,47,55,68` uses ucontext; `include/rex/thread/fiber.h:17-26,59-67` includes `<ucontext.h>`. Used by `src/kernel/crt/threading.cpp:243,328` and `src/system/xthread.cpp:644`. | Bionic has **no** `getcontext`/`makecontext`/`swapcontext` (NDK r28b `sys/ucontext.h` only defines `ucontext_t`/`mcontext_t`; `llvm-nm -D .../26/libc.so` finds none). Hard compile/link + runtime blocker. |
| Exception/SEH handling | **YES** | `src/core/exception_handler_posix.cpp:112-162,356-416` has a complete `REX_ARCH_ARM64` `/ Linux` branch reading `mcontext.regs/sp/pc/pstate/__reserved` + `fpsimd_context`/`esr_context`; all types/magics exist in NDK `sys/ucontext.h` + `aarch64-linux-android/asm/sigcontext.h`. `src/core/seh_posix.cpp` is signal-based and portable. | None. |
| Byte-swap / SIMD | **YES** | `src/core/memory.cpp:20-21` uses `<arm_neon.h>`; `:46` AMD64 / `:205` ARM64 (`vqtbl1q_u8`). `src/graphics/primitive_processor.cpp` has AMD64/ARM64 branches (e.g. `:955-960,1015-1035`). `src/audio/xma_context.cpp:755/800` AMD64 guarded with scalar `#else`. `include/rex/platform/fpscr.h:53-83` aarch64 `mrs fpcr`/`msr fpcr`. `include/rex/ppc/intrinsics.h:25,246,296,327` aarch64. | None. `simde` is a linked include dir (`thirdparty/CMakeLists.txt:98-103`) and provides x86→NEON. |
| Filesystem VFS (host path) | **PARTIAL** | `src/filesystem/devices/host_path_device.cpp` uses `std::filesystem`; `src/core/filesystem_posix.cpp` uses `fopen`/`readdir`/`stat`. These work for app-private directories. | `GetExecutablePath` uses `/proc/self/exe` (`src/core/filesystem_posix.cpp:94-99`) → `/system/bin/app_process*`; `GetUserFolder` uses `XDG_DATA_HOME`/`HOME`/`getpwuid_r` (`:106-124`) → not an Android app-private data dir. No `AAssetManager` path, so assets packaged in the APK cannot be read. |
| Android content URI (`content://`) | **NO** | Declared `include/rex/filesystem.h:129-134`; consumed `src/core/mapped_memory_posix.cpp:137`. | No implementation anywhere (`OpenAndroidContentFileDescriptor`, `IsAndroidContentUri`, `rex::filesystem::AndroidInitialize/AndroidShutdown` are undefined). `IsAndroidContentUri` and `MappedMemory::OpenForAndroidContentUri` have no callers — support is entirely unwired. |
| Android system init | **NO** | Declared `include/rex/system.h:22-24` (`InitializeAndroidSystemForApplicationContext`/`ShutdownAndroidSystem`). | No definition and no caller anywhere in the tree. |
| Windowing (SDL3) | **NO** (Android) | `src/ui/window_sdl.cpp:31-41`: `#else` (Android) includes `<X11/Xlib-xcb.h>` + `<rex/ui/surface_gnulinux.h>`. `CreateSurfaceImpl` (`:375-423`) only handles Win32/Mac/Wayland/Xcb. `GetNativeWindowHandle` (`:231-241`) returns non-null **only** on Win32. | No `ANativeWindow` path; X11 headers make it non-compilable on Android. `include/rex/ui/window_sdl.h:34` override is fine. |
| Surface abstraction | **PARTIAL** | `include/rex/ui/surface.h:28-42` already defines `kTypeIndex_AndroidNativeWindow` / `kTypeFlag_AndroidNativeWindow`. | `include/rex/ui/surface_android.h` **does not exist**, yet `src/ui/vulkan/vulkan_presenter.cpp:37` includes it and `:800-810` casts to `AndroidNativeWindowSurface`. Compile error. |
| Vulkan instance | **YES** | `include/rex/ui/vulkan/api.h:29-33` defines `VK_USE_PLATFORM_ANDROID_KHR`; `include/rex/ui/vulkan/functions/instance_khr_android_surface.inc:2` declares `vkCreateAndroidSurfaceKHR`; `src/ui/vulkan/vulkan_instance.cpp:153-157` requests `VK_KHR_android_surface`; loader name `libvulkan.so` at `include/rex/platform/dynlib.h:56-60`. | None structurally (Android loads the platform `libvulkan.so`; no vendored loader needed). |
| Vulkan presenter | **PARTIAL** | `src/ui/vulkan/vulkan_presenter.cpp:36-38` (android include), `:426-428` (`kTypeFlag_AndroidNativeWindow`), `:799-810` (`VkAndroidSurfaceCreateInfoKHR`), `:1224-1225` (R8G8B8A8 on Android). | Depends on the missing `surface_android.h` / `AndroidNativeWindowSurface`. |
| GPU plugin loader | **PARTIAL** | `src/system/gpu_plugin_loader.cpp:34-45` picks `librexgpu-<name>.so` on non-Win/Mac (correct name); `include/rex/platform/dynlib.h:56-60` has the Android library names. | `:48` resolves the plugin relative to `GetExecutableFolder()` (`/proc/self/exe`), and `:57` `dlopen`s an absolute path. On Android the plugin lives inside the APK (`lib/arm64-v8a/`), so this path is wrong; must load by soname. |
| App entry / lifecycle | **PARTIAL** | `include/rex/ui/windowed_app.h:27-30` enables `XE_UI_WINDOWED_APPS_IN_LIBRARY` for Android; `:126-183` provides the creator registry and `REX_DEFINE_APP`. | `src/ui/windowed_app_main_sdl.cpp:58` calls `rex::ui::GetWindowedAppCreator()`, which is only declared when `XE_UI_WINDOWED_APPS_IN_LIBRARY==0` (`windowed_app.h:176-183`) → does not compile on Android. No `android_main`/`SDL_main` entry (`SDL_main`/`SDL_MAIN_HANDLED` never referenced). |
| Input | **PARTIAL** | `src/input/CMakeLists.txt:30-34` uses the SDL driver on non-Win32; SDL3 provides Android joystick/touch. | No SDK-specific gap beyond SDL3/SDL integration. |
| Audio | **PARTIAL** | `src/audio/CMakeLists.txt` builds the SDL backend on all platforms; SDL3 routes Android audio internally (AAudio/OpenSL ES). XMA decode has scalar fallback (`src/audio/xma_context.cpp:800`). | SDK never calls `AAudio`/`AAssetManager` directly (grep: none). Fine as long as SDL3 audio is enabled and packaged. |
| FFmpeg (XMA) | **YES** | `thirdparty/FFmpeg/config.h` selects `config_android_aarch64.h` under `__ANDROID__` (`config.h:12-19`); that file exists. ARM64 NEON sources/defines gated by `IS_ARM64` (`thirdparty/CMakeLists.txt:446-448,543-554,665-681`). | None. (Android clang does not define `__gnu_linux__`, so the `config.h` ordering is correct.) |
| UI CMake dependencies | **NO** | `src/ui/CMakeLists.txt:149-163`: `if(UNIX AND NOT APPLE)` does `pkg_check_modules(X11_XCB REQUIRED x11-xcb)` and `pkg_check_modules(WAYLAND REQUIRED wayland-client)` and compiles `surface_gnulinux.cpp` (which includes `<xcb/xcb.h>`). | Android is `UNIX`, so configure fails (or picks host X11/Wayland headers). Must be guarded with `ANDROID`. |
| Third-party (SDL3) CMake options | **PARTIAL** | `thirdparty/CMakeLists.txt:293-302`: `if(UNIX AND NOT APPLE)` forces `SDL_X11/WAYLAND/ALSA/PULSEAUDIO/PIPEWIRE ON`. Vendored SDL3 supports Android (`thirdparty/sdl3/CMakeLists.txt:1430-1614`, `SDL_PLATFORM_ANDROID` at `sdl3/include/SDL3/SDL_platform_defines.h:111`). | Forcing desktop backends on Android is wrong and may pull host libs. Needs `ANDROID` exclusion. |
| Vendored loader/MoltenVK | **N/A** | `thirdparty/CMakeLists.txt:204-219` builds loader/MoltenVK only on `APPLE`. | Android uses the system `libvulkan.so`. |
| dlopen paths | **PARTIAL** | `src/core/dynlib_posix.cpp:27-31` — generic `dlopen(path)`. | GPU plugin path (above); Vulkan/SPIRV/RenderDoc names already Android-aware (`dynlib.h:56-60`). |

---

## 2. Prioritized gap list

### P0 — make an Android build impossible (configure/compile/link)

1. **UI CMake hard-requires desktop X11/Wayland.**
   `src/ui/CMakeLists.txt:149-163` runs on Android (`UNIX`) and requires `x11-xcb` + `wayland-client` via pkg-config, and compiles `surface_gnulinux.cpp` (`:76-78`) which includes `<xcb/xcb.h>` (`include/rex/ui/surface_gnulinux.h:15`). Configure fails before any object is built.

2. **`window_sdl.cpp` includes X11 unconditionally for Android.**
   `src/ui/window_sdl.cpp:38-41` `#else` branch includes `<X11/Xlib-xcb.h>`; `CreateSurfaceImpl` (`:401-421`) uses Wayland/Xcb types. No `ANativeWindow` path.

3. **Missing `include/rex/ui/surface_android.h` / `AndroidNativeWindowSurface`.**
   Included at `src/ui/vulkan/vulkan_presenter.cpp:37`, used at `:800-810`. File does not exist (`git ls-files | grep android` → only `instance_khr_android_surface.inc`).

4. **Missing `include/rex/main_android.h` and helpers `rex::GetAndroidApiLevel()` / `pthread_gettid_np()`.**
   `src/core/threading_posix.cpp:48` includes the header; `:74` and `src/core/memory_posix.cpp:94` call `GetAndroidApiLevel()`; `src/core/threading_posix.cpp:778,811,879` call `pthread_gettid_np()`. None are declared/defined anywhere in the tree.

5. **Undefined `rex::filesystem::OpenAndroidContentFileDescriptor`.**
   Declared `include/rex/filesystem.h:133`, called `src/core/mapped_memory_posix.cpp:137`. Also undefined: `rex::filesystem::AndroidInitialize/AndroidShutdown` (`filesystem.h:130-131`) and `InitializeAndroidSystemForApplicationContext`/`ShutdownAndroidSystem` (`include/rex/system.h:23-24`).

6. **No `ucontext` on Bionic → fibers cannot be implemented with the POSIX backend.**
   `src/core/fiber_posix.cpp:32,47,55,68`; `include/rex/thread/fiber.h:23,60`. Bionic exposes `ucontext_t` but not `getcontext`/`makecontext`/`swapcontext`. Fibers are required by the guest-thread scheduler (`src/kernel/crt/threading.cpp:243`, `src/system/xthread.cpp:644`).

7. **`windowed_app_main_sdl.cpp` cannot compile with the Android app model.**
   `src/ui/windowed_app_main_sdl.cpp:58` calls `GetWindowedAppCreator()`, which is `#if !XE_UI_WINDOWED_APPS_IN_LIBRARY` only (`include/rex/ui/windowed_app.h:176-183`), while Android sets that macro to 1 (`:27-30`). No `android_main`/`SDL_main` entry exists.

### P1 — builds after P0 but misbehaves

8. **SDL3 forced to desktop backends on Android.** `thirdparty/CMakeLists.txt:293-302`.

9. **GPU plugin load path.** `src/system/gpu_plugin_loader.cpp:34-48` uses `/proc/self/exe` + absolute `dlopen`; on Android the plugin is an APK `lib/arm64-v8a/*.so` and must be loaded by soname.

10. **App-private storage + APK assets.** `src/core/filesystem_posix.cpp:72-124` (`GetExecutablePath`, `GetUserFolder`) target desktop paths; no `AAssetManager`, so files under `assets/` cannot be opened and `content://` URIs are unsupported.

### P2 — polish / correctness

11. Root CMake platform naming for Android (`CMakeLists.txt:161-169`) — not a blocker.
12. `rex::filesystem::IsAndroidContentUri` and `MappedMemory::OpenForAndroidContentUri` are dead code until content-URI support is wired into `HostPathDevice`.

### Structural blockers (summary)
- **X11/Wayland coupling in `src/ui`** (P0 #1–2) and **missing Android headers/symbols** (P0 #3–5) prevent the build from getting past configure/compile.
- **ucontext fibers** (P0 #6) cannot be satisfied by POSIX on Bionic; requires either a ucontext shim (not provided by Android) or porting `Fiber` to a Bionic-compatible switch (e.g. assembly / `setjmp`-based).
- Nothing here requires x86 codegen: the recompiler emits PPC→C++ and all host-SIMD code is already guarded (`REX_ARCH_AMD64`/`REX_ARCH_ARM64`).

### Labeled speculation
- (speculative) `MAP_FIXED` reservations for guest memory (`src/core/memory_posix.cpp:498-528`) may conflict with Android/Scudo address-space layout or SELinux on some devices; this is a runtime risk, not proven by reading.
- (speculative) Bionic's `pthread_getname_np` is listed as `@@LIBC_O` (unversioned/obsolete marker) in the API 26 stub; the SDK already dlopens it and falls back before API 26, so this is likely harmless.
