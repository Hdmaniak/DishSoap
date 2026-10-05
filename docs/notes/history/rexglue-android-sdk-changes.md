# Proposed ReXGlue SDK changes for Android arm64

**Status: PROPOSED — NOT APPLIED.** `<rexglue-sdk>` is owned by
another agent; nothing here has been written to it.

Unified diffs are relative to the SDK root. Each patch lists a rationale and the
audit finding it closes (see `notes/android-support-audit.md`). Patches are
ordered so the tree can be configured and compiled incrementally.

---

## Patch 01 — Guard desktop X11/Wayland in `src/ui/CMakeLists.txt`  (P0 #1)

**Rationale.** `if(UNIX AND NOT APPLE)` also matches Android. It hard-requires
`x11-xcb` + `wayland-client` via pkg-config and compiles `surface_gnulinux.cpp`
(`<xcb/xcb.h>`), so the Android configure/compile fails. Select an Android
surface source and only enable X11/Wayland on desktop Linux.

```diff
--- a/src/ui/CMakeLists.txt
+++ b/src/ui/CMakeLists.txt
@@ -66,10 +66,14 @@
 # Platform-specific sources (windowing itself is SDL3, platform-independent)
 if(WIN32)
     set(REXUI_PLATFORM_SOURCES
         surface_win.cpp
     )
+elseif(ANDROID)
+    set(REXUI_PLATFORM_SOURCES
+        surface_android.cpp
+    )
 elseif(APPLE)
     set(REXUI_PLATFORM_SOURCES
         surface_mac.cpp
     )
 else()
     set(REXUI_PLATFORM_SOURCES
         surface_gnulinux.cpp
     )
 endif()
@@ -146,7 +150,7 @@
 endif()
 
 # Platform-specific dependencies
-if(UNIX AND NOT APPLE)
+if(UNIX AND NOT APPLE AND NOT ANDROID)
     find_package(PkgConfig REQUIRED)
     pkg_check_modules(X11_XCB REQUIRED x11-xcb)
     # Required, not optional: vulkan/api.h pulls in <wayland-client.h> on every
     # Linux build.
     pkg_check_modules(WAYLAND REQUIRED wayland-client)
```

Companion: add `src/ui/surface_android.cpp` (see Patch 03b). If you prefer not
to add a .cpp, keep `REXUI_PLATFORM_SOURCES` empty for Android — the header is
the only thing the presenter needs.

---

## Patch 02 — Don't force SDL3 desktop backends on Android  (P1 #8)

**Rationale.** `thirdparty/CMakeLists.txt:293-302` sets `SDL_X11`,
`SDL_WAYLAND`, `SDL_ALSA`, `SDL_PULSEAUDIO`, `SDL_PIPEWIRE` to `ON` for any
`UNIX`. On Android those are wrong and can drag in host libraries. Vendored
SDL3 already has full Android support; leave the backend selection to SDL.

```diff
--- a/thirdparty/CMakeLists.txt
+++ b/thirdparty/CMakeLists.txt
@@ -290,7 +290,7 @@
 # Linux video and audio backends. Both video drivers are always built so the
 # one in use stays a runtime choice.
-if(UNIX AND NOT APPLE)
+if(UNIX AND NOT APPLE AND NOT ANDROID)
     set(SDL_X11               ON  CACHE BOOL "" FORCE)
     set(SDL_WAYLAND           ON  CACHE BOOL "" FORCE)
     set(SDL_ALSA              ON  CACHE BOOL "" FORCE)
```

---

## Patch 03 — Add `include/rex/ui/surface_android.h`  (P0 #3)

**Rationale.** `src/ui/vulkan/vulkan_presenter.cpp:37` includes this header and
`:800-810` constructs/uses `AndroidNativeWindowSurface`, but the file does not
exist. It mirrors `surface_mac.h`/`surface_win.h` using `ANativeWindow`.

```diff
--- /dev/null
+++ b/include/rex/ui/surface_android.h
@@ -0,0 +1,44 @@
+/**
+ * Android native window surface (ANativeWindow -> VK_KHR_android_surface).
+ */
+
+#pragma once
+
+#include <rex/ui/surface.h>
+
+#include <android/native_window.h>
+
+namespace rex {
+namespace ui {
+
+class AndroidNativeWindowSurface final : public Surface {
+ public:
+  explicit AndroidNativeWindowSurface(ANativeWindow* window) : window_(window) {
+    if (window_) {
+      ANativeWindow_acquire(window_);
+    }
+  }
+  ~AndroidNativeWindowSurface() override {
+    if (window_) {
+      ANativeWindow_release(window_);
+      window_ = nullptr;
+    }
+  }
+
+  TypeIndex GetType() const override { return kTypeIndex_AndroidNativeWindow; }
+
+  // Consumed by vulkan_presenter.cpp for VkAndroidSurfaceCreateInfoKHR::window.
+  ANativeWindow* window() const { return window_; }
+
+ protected:
+  bool GetSizeImpl(uint32_t& width_out, uint32_t& height_out) const override {
+    if (!window_) {
+      return false;
+    }
+    width_out = uint32_t(ANativeWindow_getWidth(window_));
+    height_out = uint32_t(ANativeWindow_getHeight(window_));
+    return width_out != 0 && height_out != 0;
+  }
+
+ private:
+  ANativeWindow* window_ = nullptr;
+};
+
+}  // namespace ui
+}  // namespace rex
```

### Patch 03b — Add `src/ui/surface_android.cpp` (only if CMake lists it)

`GetSizeImpl` above is inline, so a .cpp is optional. If Patch 01 lists
`surface_android.cpp`, add:

```diff
--- /dev/null
+++ b/src/ui/surface_android.cpp
@@ -0,0 +1,12 @@
+// All Android surface behavior is inline in surface_android.h; this TU exists
+// only so the CMake source list has a platform file like the other platforms.
+
+#include <rex/ui/surface_android.h>
```

---

## Patch 04 — Android branch in `src/ui/window_sdl.cpp`  (P0 #2)

**Rationale.** The Android path currently falls into the `#else` X11/Wayland
branch. Add an `ANativeWindow` branch for both includes, native-handle
retrieval, and surface creation. SDL3 exposes the window via
`SDL_PROP_WINDOW_ANDROID_WINDOW_POINTER`
(`thirdparty/sdl3/include/SDL3/SDL_video.h:1641`).

```diff
--- a/src/ui/window_sdl.cpp
+++ b/src/ui/window_sdl.cpp
@@ -29,13 +29,16 @@
 #include <rex/ui/sdl_virtual_key.h>
 
 #if REX_PLATFORM_WIN32
 #include <rex/ui/surface_win.h>
+#elif REX_PLATFORM_ANDROID
+#include <android/native_window.h>
+#include <rex/ui/surface_android.h>
 #elif REX_PLATFORM_MAC
 #include <CoreFoundation/CoreFoundation.h>
 #include <SDL3/SDL_metal.h>
 
 #include <rex/ui/surface_mac.h>
 #else
 #include <X11/Xlib-xcb.h>
 #include <rex/ui/surface_gnulinux.h>
 #endif
@@ -231,8 +234,17 @@ void* WindowSDL::GetNativeWindowHandle() const {
 #if REX_PLATFORM_WIN32
   if (!sdl_window_) {
     return nullptr;
   }
   return SDL_GetPointerProperty(SDL_GetWindowProperties(sdl_window_),
                                 SDL_PROP_WINDOW_WIN32_HWND_POINTER, nullptr);
+#elif REX_PLATFORM_ANDROID
+  if (!sdl_window_) {
+    return nullptr;
+  }
+  return SDL_GetPointerProperty(SDL_GetWindowProperties(sdl_window_),
+                                SDL_PROP_WINDOW_ANDROID_WINDOW_POINTER, nullptr);
 #else
   return nullptr;
 #endif
 }
@@ -379,6 +391,15 @@ std::unique_ptr<Surface> WindowSDL::CreateSurfaceImpl(Surface::TypeFlags allowed
 #if REX_PLATFORM_WIN32
   SDL_PropertiesID props = SDL_GetWindowProperties(sdl_window_);
   if (allowed_types & Surface::kTypeFlag_Win32Hwnd) {
     HWND hwnd = static_cast<HWND>(
         SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WIN32_HWND_POINTER, nullptr));
     HINSTANCE hinstance = static_cast<HINSTANCE>(
         SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WIN32_INSTANCE_POINTER, nullptr));
     if (hwnd) {
       return std::make_unique<Win32HwndSurface>(hinstance, hwnd);
     }
   }
+#elif REX_PLATFORM_ANDROID
+  if (allowed_types & Surface::kTypeFlag_AndroidNativeWindow) {
+    auto* native_window = static_cast<ANativeWindow*>(SDL_GetPointerProperty(
+        SDL_GetWindowProperties(sdl_window_),
+        SDL_PROP_WINDOW_ANDROID_WINDOW_POINTER, nullptr));
+    if (native_window) {
+      return std::make_unique<AndroidNativeWindowSurface>(native_window);
+    }
+  }
 #elif REX_PLATFORM_MAC
   if (allowed_types & Surface::kTypeFlag_CAMetalLayer) {
     SDL_MetalView metal_view = SDL_Metal_CreateView(sdl_window_);
```

**Note.** After this, the `#else` branch is desktop-Linux only. Consider
changing it to `#elif REX_PLATFORM_GNU_LINUX` for symmetry with
`include/rex/ui/vulkan/api.h:35`, so Android can never fall through to X11 again.

---

## Patch 05 — Add `include/rex/main_android.h` (helpers referenced by core)  (P0 #4)

**Rationale.** `src/core/threading_posix.cpp:48` includes this header; it and
`src/core/memory_posix.cpp:94` call `rex::GetAndroidApiLevel()`, and
`src/core/threading_posix.cpp:778,811,879` call `pthread_gettid_np()`. None
exist. Bionic provides `android_get_device_api_level()` (`<android/api-level.h>`)
and `SYS_gettid` (`<sys/syscall.h>`).

```diff
--- /dev/null
+++ b/include/rex/main_android.h
@@ -0,0 +1,33 @@
+#pragma once
+/**
+ * Small Android/NDK shims for the POSIX core.  Kept inline so no extra object
+ * has to be linked (matches how the pre-API-26 code paths in
+ * src/core/threading_posix.cpp are already written).
+ */
+
+#include <rex/platform.h>
+
+#if REX_PLATFORM_ANDROID
+
+#include <android/api-level.h>
+#include <pthread.h>
+#include <sys/syscall.h>
+#include <unistd.h>
+
+namespace rex {
+
+// Device API level (e.g. 26).  Bionic-only.
+inline int GetAndroidApiLevel() {
+  return android_get_device_api_level();
+}
+
+}  // namespace rex
+
+// Bionic has no pthread_gettid_np; route to the gettid syscall.  Declared at
+// global scope because the call sites use the unqualified pthread_* name.
+inline int pthread_gettid_np(pthread_t) {
+  return static_cast<int>(syscall(SYS_gettid));
+}
+
+#endif  // REX_PLATFORM_ANDROID
```

---

## Patch 06 — Implement the declared Android filesystem/system entry points  (P0 #5, P1 #10)

**Rationale.** `include/rex/filesystem.h:129-134` and `include/rex/system.h:22-24`
declare symbols that are never defined, while
`src/core/mapped_memory_posix.cpp:137` links against
`OpenAndroidContentFileDescriptor`. `content://` resolution needs the app's JNI
context, so add a minimal registration hook the port calls from its Activity,
then resolve content URIs through `ContentResolver.openFileDescriptor`.

This patch is intentionally self-contained in one new translation unit plus a
tiny header. **Alternative:** the port project can supply these symbols instead
of patching the SDK; either works.

```diff
--- /dev/null
+++ b/include/rex/android_jni.h
@@ -0,0 +1,20 @@
+#pragma once
+
+#include <rex/platform.h>
+
+#if REX_PLATFORM_ANDROID
+
+#include <jni.h>
+
+namespace rex::android {
+
+// Must be called once from the app's JNI_OnLoad/Activity before any content://
+// access.  Stores a global ref to the Context and the AssetManager.
+void SetApplicationContext(JNIEnv* env, jobject context);
+void ClearApplicationContext(JNIEnv* env);
+
+}  // namespace rex::android
+
+#endif  // REX_PLATFORM_ANDROID
```

```diff
--- /dev/null
+++ b/src/core/filesystem_android.cpp
@@ -0,0 +1,120 @@
+// Android implementations of the compatibility shims declared in
+// rex/filesystem.h and rex/system.h.  Only compiled when REX_PLATFORM_ANDROID.
+
+#include <rex/platform.h>
+
+#if REX_PLATFORM_ANDROID
+
+#include <android/asset_manager.h>
+#include <android/asset_manager_jni.h>
+#include <jni.h>
+
+#include <string>
+
+#include <rex/android_jni.h>
+#include <rex/filesystem.h>
+#include <rex/logging.h>
+#include <rex/memory/utils.h>
+#include <rex/system.h>
+#include <rex/thread.h>
+
+namespace {
+
+JavaVM* g_vm = nullptr;
+jobject g_context = nullptr;   // global ref
+AAssetManager* g_assets = nullptr;
+
+JNIEnv* Env() {
+  JNIEnv* env = nullptr;
+  if (g_vm) {
+    g_vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6);
+  }
+  return env;
+}
+
+}  // namespace
+
+namespace rex::android {
+
+void SetApplicationContext(JNIEnv* env, jobject context) {
+  env->GetJavaVM(&g_vm);
+  if (g_context) {
+    env->DeleteGlobalRef(g_context);
+  }
+  g_context = env->NewGlobalRef(context);
+  g_assets = AAssetManager_fromJava(env, context);
+}
+
+void ClearApplicationContext(JNIEnv* env) {
+  if (g_context) {
+    env->DeleteGlobalRef(g_context);
+    g_context = nullptr;
+  }
+  g_assets = nullptr;
+  g_vm = nullptr;
+}
+
+}  // namespace rex::android
+
+namespace rex::filesystem {
+
+void AndroidInitialize() {}
+void AndroidShutdown() {}
+
+bool IsAndroidContentUri(const std::string_view source) {
+  return source.rfind("content://", 0) == 0;
+}
+
+int OpenAndroidContentFileDescriptor(const std::string_view uri, const char* mode) {
+  JNIEnv* env = Env();
+  if (!env || !g_context) {
+    REXFS_ERROR("OpenAndroidContentFileDescriptor: no application context");
+    return -1;
+  }
+  // ContentResolver resolver = context.getContentResolver();
+  jclass ctx_cls = env->GetObjectClass(g_context);
+  jmethodID get_resolver = env->GetMethodID(
+      ctx_cls, "getContentResolver", "()Landroid/content/ContentResolver;");
+  jobject resolver = env->CallObjectMethod(g_context, get_resolver);
+  // ParcelFileDescriptor pfd = resolver.openFileDescriptor(uri, mode);
+  jclass res_cls = env->GetObjectClass(resolver);
+  jmethodID open = env->GetMethodID(
+      res_cls, "openFileDescriptor",
+      "(Landroid/net/Uri;Ljava/lang/String;)Landroid/os/ParcelFileDescriptor;");
+  jstring j_uri_str = env->NewStringUTF(std::string(uri).c_str());
+  jclass uri_cls = env->FindClass("android/net/Uri");
+  jmethodID parse = env->GetStaticMethodID(uri_cls, "parse",
+                                           "(Ljava/lang/String;)Landroid/net/Uri;");
+  jobject j_uri = env->CallStaticObjectMethod(uri_cls, parse, j_uri_str);
+  jstring j_mode = env->NewStringUTF(mode);
+  jobject pfd = env->CallObjectMethod(resolver, open, j_uri, j_mode);
+  if (env->ExceptionCheck() || !pfd) {
+    env->ExceptionClear();
+    return -1;
+  }
+  // int fd = pfd.detachFd();
+  jclass pfd_cls = env->GetObjectClass(pfd);
+  jmethodID detach = env->GetMethodID(pfd_cls, "detachFd", "()I");
+  return env->CallIntMethod(pfd, detach);
+}
+
+}  // namespace rex::filesystem
+
+namespace rex {
+
+bool InitializeAndroidSystemForApplicationContext() {
+  rex::memory::AndroidInitialize();
+  rex::thread::AndroidInitialize();
+  return true;
+}
+
+void ShutdownAndroidSystem() {
+  rex::thread::AndroidShutdown();
+  rex::memory::AndroidShutdown();
+}
+
+}  // namespace rex
+
+#endif  // REX_PLATFORM_ANDROID
```

Then register the new source in `src/core/CMakeLists.txt`:

```diff
--- a/src/core/CMakeLists.txt
+++ b/src/core/CMakeLists.txt
@@ -56,6 +56,9 @@
 elseif(UNIX)
     # Linux and macOS/Darwin share the POSIX sources; Darwin-specific behavior
     # is selected inside them with #if defined(__APPLE__).
+    if(ANDROID)
+        target_sources(rexcore PRIVATE filesystem_android.cpp)
+    endif()
     target_sources(rexcore PRIVATE
         atomic_posix.cpp
         clock_posix.cpp
```

---

## Patch 07 — Android app entry in `src/ui/windowed_app_main_sdl.cpp`  (P0 #7)

**Rationale.** On Android `XE_UI_WINDOWED_APPS_IN_LIBRARY` is 1
(`include/rex/ui/windowed_app.h:27-30`), so `rex::ui::GetWindowedAppCreator()`
is not declared (`:176-183`) and this file does not compile. SDL3's Android
`SDLActivity` forwards to `SDL_main`; the port selects the app by identifier.
Make the creator lookup conditional and expose an entry the port can call.

```diff
--- a/src/ui/windowed_app_main_sdl.cpp
+++ b/src/ui/windowed_app_main_sdl.cpp
@@ -55,8 +55,14 @@ int RunWindowedApp(int argc, char** argv) {
         return EXIT_FAILURE;
     }
 #endif
 
+#if XE_UI_WINDOWED_APPS_IN_LIBRARY
+    // Android: the Java SDLActivity chooses an app identifier; the port's
+    // android_main/SDL_main passes it in (empty -> first registered app).
+    const char* app_id = std::getenv("REXGLUE_APP");
+    auto creator = rex::ui::WindowedApp::GetCreator(app_id ? app_id : "");
+    std::unique_ptr<rex::ui::WindowedApp> app =
+        creator ? creator(app_context) : nullptr;
+#else
     std::unique_ptr<rex::ui::WindowedApp> app = rex::ui::GetWindowedAppCreator()(app_context);
+#endif
+    if (!app) {
+      return EXIT_FAILURE;
+    }
 
     // Match remaining positional args to the app's expected options.
     const auto& option_names = app->GetPositionalOptions();
@@ -125,7 +131,18 @@ int WINAPI wWinMain(HINSTANCE hinstance, HINSTANCE hinstance_prev, LPWSTR comman
   return RunWindowedApp(static_cast<int>(argv_ptrs.size()), argv_ptrs.data());
 }
 
 #else
 
 int main(int argc, char* argv[]) {
   return RunWindowedApp(argc, argv);
 }
 
 #endif
+
+#if REX_PLATFORM_ANDROID
+// Called by the port's SDL_main / JNI bridge.  SDL3's Java SDLActivity invokes
+// SDL_main, which the port maps to rex_android_main.
+extern "C" int rex_android_main(int argc, char** argv) {
+  return RunWindowedApp(argc, argv);
+}
+#endif
```

The port must (a) provide `#include <SDL3/SDL_main.h>` in its own entry TU, and
(b) either register a single `REX_DEFINE_APP("dishwasher", ...)` app or set
`REXGLUE_APP`. (`GetCreator` lives in `windowed_app.h:153`.)

---

## Patch 08 — Load the GPU plugin by soname on Android  (P1 #9)

**Rationale.** `src/system/gpu_plugin_loader.cpp:48` builds the plugin path from
`GetExecutableFolder()` (`/proc/self/exe`) and `:57` `dlopen`s it by absolute
path. On Android the plugin lives inside the APK
(`lib/arm64-v8a/librexgpu-xenos.so`) and can only be opened by soname.

```diff
--- a/src/system/gpu_plugin_loader.cpp
+++ b/src/system/gpu_plugin_loader.cpp
@@ -46,15 +46,24 @@ std::unique_ptr<IGraphicsSystem> LoadGpuPlugin(std::string_view name, std::string
   auto path = rex::filesystem::GetExecutableFolder() / PluginFileName(name);
+#if REX_PLATFORM_ANDROID
+  // The plugin ships inside the APK; the dynamic linker resolves it by soname
+  // from the app's native library namespace, never by path.
+  const std::string soname = PluginFileName(name);
+  platform::DynamicLibrary library;
+  if (!library.Load(soname, platform::SymbolResolution::kImmediate)) {
+    REXSYS_ERROR("GPU plugin '{}' failed to load ({})", name, soname);
+    return nullptr;
+  }
+#else
   if (!std::filesystem::exists(path)) {
     REXSYS_ERROR(
         "GPU plugin '{}' not found at {}. Stage it next to the executable "
         "(GPU_PLUGINS {} in rexglue_configure_target).",
         name, path.string(), name);
     return nullptr;
   }
 
   platform::DynamicLibrary library;
   if (!library.Load(path, platform::SymbolResolution::kImmediate)) {
     REXSYS_ERROR("GPU plugin '{}' failed to load: {}", name, path.string());
     return nullptr;
   }
+#endif
   auto abi_version_fn = library.GetSymbol<GpuAbiVersionFn>(kGpuAbiVersionSymbol);
```

`PluginFileName` already returns the correct `.so` name on non-Win/Mac
(`src/system/gpu_plugin_loader.cpp:34-45`), and the OS `dlopen` search path
includes the app's `lib/arm64-v8a` directory.

---

## Patch 09 — Android-aware executable/user directories in `src/core/filesystem_posix.cpp`  (P1 #10)

**Rationale.** `/proc/self/exe` is `/system/bin/app_process*` on Android, and
`XDG_DATA_HOME`/`HOME`/`getpwuid_r` do not point at the app's private storage.
Add Android branches. (`GetAndroidApiLevel` from Patch 05; app data dir comes
from the JNI context added in Patch 06 if desired, otherwise `PATH_MAX`-style
fallback is not meaningful, so this patch relies on the context.)

```diff
--- a/src/core/filesystem_posix.cpp
+++ b/src/core/filesystem_posix.cpp
@@ -71,6 +71,10 @@ namespace filesystem {
 std::filesystem::path GetExecutablePath() {
+#if defined(__ANDROID__)
+  // /proc/self/exe is app_process on Android; the native library directory is
+  // what callers actually want for CWD-relative default paths.
+  return {};
+#else
 #if defined(__APPLE__)
   // Darwin has no /proc; query the executable path via the dyld API. The first
   // call reports the required buffer size.
@@ -97,6 +101,7 @@ std::filesystem::path GetExecutablePath() {
   std::string s(buff);
   return s;
 #endif
+#endif
 }
 
 std::filesystem::path GetExecutableFolder() {
@@ -105,6 +110,11 @@ std::filesystem::path GetExecutableFolder() {
 
 std::filesystem::path GetUserFolder() {
+#if defined(__ANDROID__)
+  // Callers should set user_data_root via a cvar or provide an app-private dir
+  // from the Java context. Empty here so defaults stay explicit.
+  return {};
+#else
   // get preferred data home
   if (auto xdg = rex::platform::env::get("XDG_DATA_HOME")) {
     return std::filesystem::path(*xdg);
@@ -122,6 +132,7 @@ std::filesystem::path GetUserFolder() {
   assert(&pw1 == pw);  // sanity check
   return std::filesystem::path(pw->pw_dir) / ".local" / "share";
+#endif
 }
```

> This is the smallest edit that keeps Android compiling. The port is expected
> to set `--game_data_root`/`--user_data_root` to the app-private dir
> (`context.getFilesDir()` / `getExternalFilesDir()`), which `ReXApp` already
> supports (`src/ui/rex_app.cpp:108-137`).

---

## Patch 10 — Make the root platform name explicit for Android  (P2 #11)

**Rationale.** Purely cosmetic/correctness: Android currently reports as
`linux-arm64` (`CMakeLists.txt:161-169`); output paths and logs should say
`android-arm64`.

```diff
--- a/CMakeLists.txt
+++ b/CMakeLists.txt
@@ -158,7 +158,13 @@ if(WIN32)
     endif()
     add_compile_definitions(REX_PLATFORM_WINDOWS=1)
+elseif(ANDROID)
+    if(REX_TARGET_PROCESSOR MATCHES "aarch64|ARM64|arm64")
+        set(REX_PLATFORM "android-arm64")
+    else()
+        set(REX_PLATFORM "android-${REX_TARGET_PROCESSOR}")
+    endif()
+    add_compile_definitions(REX_PLATFORM_LINUX=1)
 elseif(UNIX AND NOT APPLE)
     if(REX_TARGET_PROCESSOR MATCHES "AMD64|x86_64")
         set(REX_PLATFORM "linux-amd64")
```

---

## Patch 11 — Fiber backend for Bionic (largest work item)  (P0 #6)

**Rationale.** `src/core/fiber_posix.cpp` uses `getcontext`/`makecontext`/
`swapcontext`, which do not exist anywhere in the NDK (verified: absent from the
API 21/24/26/29/31/34/35 `libc.so` stubs and from `sys/ucontext.h`). Fibers back
the guest-thread scheduler (`src/kernel/crt/threading.cpp:243`,
`src/system/xthread.cpp:644`), so this is a functional blocker, not just build.

There is no single-line fix. Proposed minimal approach: keep the POSIX backend
for desktop and add an aarch64 `src/core/fiber_android.cpp` that switches stacks
with a tiny `setjmp`-based trampoline (setjmp only stores the callee-saved
registers; the custom stack is installed by switching `sp` in a `naked`-style
shim). Sketch of the shape (not complete — needs validation on-device):

```diff
--- a/src/core/CMakeLists.txt
+++ b/src/core/CMakeLists.txt
@@ -63,10 +63,15 @@ elseif(UNIX)
         exception_handler_posix.cpp
         filesystem_posix.cpp
-        fiber_posix.cpp
         mapped_memory_posix.cpp
         math_gcc.cpp
         memory_posix.cpp
         platform/console_posix.cpp
@@ -74,6 +79,11 @@ elseif(UNIX)
         threading_posix.cpp
     )
+    if(ANDROID)
+        target_sources(rexcore PRIVATE fiber_android.cpp)
+    else()
+        target_sources(rexcore PRIVATE fiber_posix.cpp)
+    endif()
 endif()
```

```diff
--- /dev/null
+++ b/src/core/fiber_android.cpp
@@ -0,0 +1,40 @@
+// Android/aarch64 Fiber backend.  Bionic has no ucontext, so fibre startup is
+// bootstrapped by entering the entry function on a freshly allocated stack and
+// switching cooperative contexts with a jmp_buf pair.
+//
+// NOTE: sketch only. The stack switch must preserve the aarch64 callee-saved
+// set (x19-x28, x29, x30, d8-d15, sp) exactly like swapcontext does; a plain
+// setjmp/longjmp across stacks is not sufficient and must be completed and
+// tested on device before shipping.
+
+#include <rex/platform.h>
+#if REX_PLATFORM_ANDROID
+
+#include <rex/thread/fiber.h>
+#include <cassert>
+
+namespace rex::thread {
+
+thread_local Fiber* Fiber::tls_current_ = nullptr;
+
+// TODO(android): implement ConvertCurrentThread/Create/SwitchTo/Destroy using
+// an aarch64 context switch (inline asm or a small .S file) instead of
+// getcontext/makecontext/swapcontext.  Keep the public interface unchanged.
+
+}  // namespace rex::thread
+
+#endif  // REX_PLATFORM_ANDROID
```

Alternatives considered and rejected:
- Link a third-party ucontext implementation for Bionic (extra dependency,
  signal/green-stack subtleties).
- Switch the guest scheduler from fibers to real OS threads (much larger
  behavioral change; the runtime relies on cooperative switching semantics).

---

## Patch ordering / expected result

1. Patches 01, 02, 10 → configure succeeds with
   `-DCMAKE_TOOLCHAIN_FILE=$(pwd)/android-aarch64.cmake`.
2. Patches 03, 04, 05 → `rexui` and `rexcore` compile past the missing
   X11/header/symbol errors.
3. Patches 06, 07, 09 → link succeeds and the app has an Android entry + JNI
   context + app-private paths.
4. Patch 08 → GPU plugin is found at runtime.
5. Patch 11 → guest threads actually run. Until this exists, the binary may
   link but crash/abort the first time a guest fiber is created.
