# Proposed Vulkan/mobile fixes — ReXGlue SDK 0.10.0

> **NOT APPLIED.** These are proposals only. Do not apply to `rexglue-sdk` (read-only reference) or
> `rexglue-sdk-android` (owned by another agent). Every diff below is a *proposal*; verify with a
> real build + on-device run before adopting. Companion audit:
> `android/notes/vulkan-mobile-audit.md`.

Only concrete, defensible changes are included. Performance rewrites (R1/R2) are deliberately **not**
included because they need on-device profiling first and touch correctness-sensitive resolve paths.

---

## P1. Enable scalar/uniform-block-layout features on Vulkan 1.1 devices (R6, defensive)

**Why:** `vulkan_device.cpp:340-343` links `VkPhysicalDeviceVulkan12Features` only when
`apiVersion >= 1.2`. The A52 reports **1.1.128**, so `scalarBlockLayout` and
`uniformBufferStandardLayout` are never enabled even though the driver advertises both (and exposes
`VK_EXT_scalar_block_layout` / `VK_KHR_uniform_buffer_standard_layout`). The backend emits explicit
`DecorationOffset`s, so this is believed harmless today — but enabling the ext features removes a
silent layout assumption at zero cost when the driver supports them.

**File:** `src/ui/vulkan/vulkan_device.cpp`

Add local flags near the other `< 1.2` fallback flags (around line 206-212):

```diff
   bool ext_1_2_KHR_sampler_mirror_clamp_to_edge = false;
   bool ext_1_1_KHR_maintenance1 = false;
+  bool ext_EXT_scalar_block_layout = false;
+  bool ext_KHR_uniform_buffer_standard_layout = false;
   bool ext_1_2_KHR_shader_float_controls = false;
```

Request them as promoted-but-not-core when `< 1.2` (inside `if (with_gpu_emulation)` around line 213):

```diff
   if (with_gpu_emulation) {
     // #15.
     XE_UI_VULKAN_LOCAL_PROMOTED_EXTENSION(KHR_sampler_mirror_clamp_to_edge, 1, 2)
     // #70. Must be enabled for VK_KHR_sampler_ycbcr_conversion.
     XE_UI_VULKAN_LOCAL_PROMOTED_EXTENSION(KHR_maintenance1, 1, 1)
+    // Not core before 1.2; keep the 1.2 feature path via the EXT/KHR forms.
+    if (properties.apiVersion < VK_MAKE_API_VERSION(0, 1, 2, 0)) {
+      XE_UI_VULKAN_LOCAL_PROMOTED_EXTENSION(EXT_scalar_block_layout, 1, 2)
+      XE_UI_VULKAN_LOCAL_PROMOTED_EXTENSION(KHR_uniform_buffer_standard_layout, 1, 2)
+    }
```

Add feature structs near the other `VulkanFeatures<...>` declarations (around line 324-337):

```diff
   VulkanFeatures<VkPhysicalDeviceShaderDemoteToHelperInvocationFeaturesEXT,
                  VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_DEMOTE_TO_HELPER_INVOCATION_FEATURES_EXT>
       features_1_3_EXT_shader_demote_to_helper_invocation;
+  VulkanFeatures<VkPhysicalDeviceScalarBlockLayoutFeatures,
+                 VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SCALAR_BLOCK_LAYOUT_FEATURES>
+      features_EXT_scalar_block_layout;
+  VulkanFeatures<VkPhysicalDeviceUniformBufferStandardLayoutFeatures,
+                 VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_UNIFORM_BUFFER_STANDARD_LAYOUT_FEATURES>
+      features_KHR_uniform_buffer_standard_layout;
```

Link them in the `if (get_physical_device_properties2_supported)` block (around line 339-381):

```diff
   if (get_physical_device_properties2_supported) {
     ...
     if (ext_EXT_non_seamless_cube_map) {
       features_EXT_non_seamless_cube_map.Link(supported_features_2, device_create_info);
     }
+    if (ext_EXT_scalar_block_layout) {
+      features_EXT_scalar_block_layout.Link(supported_features_2, device_create_info);
+    }
+    if (ext_KHR_uniform_buffer_standard_layout) {
+      features_KHR_uniform_buffer_standard_layout.Link(supported_features_2, device_create_info);
+    }
```

Enable the feature bits when present (in the `1.2` / else branch, around line 648-658):

```diff
   if (properties.apiVersion >= VK_MAKE_API_VERSION(0, 1, 2, 0)) {
     if (with_gpu_emulation) {
       XE_UI_VULKAN_FEATURE_2(features_1_2, samplerMirrorClampToEdge);
       XE_UI_VULKAN_FEATURE_2(features_1_2, uniformBufferStandardLayout);
       XE_UI_VULKAN_FEATURE_2(features_1_2, scalarBlockLayout);
     }
   } else {
     if (ext_1_2_KHR_sampler_mirror_clamp_to_edge) {
       XE_UI_VULKAN_FEATURE_IMPLIED(samplerMirrorClampToEdge)
     }
+    if (with_gpu_emulation && ext_EXT_scalar_block_layout) {
+      XE_UI_VULKAN_FEATURE_2(features_EXT_scalar_block_layout, scalarBlockLayout)
+    }
+    if (with_gpu_emulation && ext_KHR_uniform_buffer_standard_layout) {
+      XE_UI_VULKAN_FEATURE_2(features_KHR_uniform_buffer_standard_layout,
+                             uniformBufferStandardLayout)
+    }
   }
```

**Risk:** low. The features are only enabled if the driver reports them; the SPIR-V descriptor
layouts emitted by the translator are unchanged either way. Verify with the GPU validation layer.

---

## P2. Guard the 512 MB guest buffer against `maxStorageBufferRange` (R7, defensive)

**Why:** A52 reports `maxStorageBufferRange = 536870912` and `SharedMemory::kBufferSize = 1<<29`
= 536870912 — an exact fit with no headroom. If the shared-memory buffer is ever grown, or the
device reports a smaller range, device creation succeeds but buffer/memory binding or descriptor
updates can fail at runtime. Add an early, explicit diagnostic.

**File:** `src/ui/vulkan/vulkan_device.cpp` (inside `CreateIfSupported`, after limits are copied,
near line 627, before device creation at 743):

```diff
   XE_UI_VULKAN_LIMIT(nonCoherentAtomSize)
+
+  // Xenia binds the entire 512 MB guest shared-memory buffer as one storage
+  // buffer (SharedMemory::kBufferSize). Fail loudly rather than at a later,
+  // harder-to-diagnose descriptor write if the device can't address it.
+  if (properties.limits.maxStorageBufferRange < (uint32_t(1) << 29)) {
+    REXLOG_ERROR(
+        "Vulkan device '{}' maxStorageBufferRange {} is smaller than the "
+        "{} byte guest shared memory buffer",
+        properties.deviceName, properties.limits.maxStorageBufferRange,
+        (uint32_t(1) << 29));
+    return nullptr;
+  }
```

**Note:** if a future change makes shared memory smaller, replace the literal with the actual
`SharedMemory::kBufferSize` (include `rex/graphics/shared_memory.h`).

---

## P3. Keep the FSI path from being selected on Adreno (R3, hardening/documentation)

**Why:** On Adreno 618 `VK_EXT_fragment_shader_interlock` is absent. The default path
(`render_target_path_vulkan == ""`) is already FBO, but an explicit `fsi` request silently leads to a
hard `return false` only if a second gate ALSO fails (`render_target_cache.cpp:218-239`). Emit a
clear warning when the user explicitly requests `fsi` on an unsupported device instead of failing
opaquely at initialization.

**File:** `src/graphics/vulkan/render_target_cache.cpp` (around lines 209-213):

```diff
   if (REXCVAR_GET(render_target_path_vulkan) == "fsi") {
     path_ = Path::kPixelShaderInterlock;
   } else {
     path_ = Path::kHostRenderTargets;
   }
+  if (path_ == Path::kPixelShaderInterlock && !fsi_path_supported) {
+    REXGPU_WARN(
+        "VulkanRenderTargetCache: fragment shader interlock was requested but is "
+        "not supported by this device; falling back to host render targets");
+    path_ = Path::kHostRenderTargets;
+  }
```

**Risk:** low; only changes logging + makes the fallback explicit. (Not required for the A52, which
uses the default path.)

---

## P4. Reference: `include/rex/ui/surface_android.h` for the platform agent (do not apply here)

**Why:** This file does not exist (`src/ui/vulkan/vulkan_presenter.cpp:37` includes it,
`:799-810` uses `AndroidNativeWindowSurface`). This is the platform agent's file; provided here only
as the exact interface the Vulkan presenter needs (see audit §7.2). **Do not add this to
`rexglue-sdk`/`rexglue-sdk-android` from this patch** — hand it to the platform agent.

```cpp
// include/rex/ui/surface_android.h
#pragma once
#include <rex/ui/surface.h>
#include <android/native_window.h>

namespace rex::ui {

class AndroidNativeWindowSurface final : public Surface {
 public:
  explicit AndroidNativeWindowSurface(ANativeWindow* window) : window_(window) {}
  TypeIndex GetType() const override { return kTypeIndex_AndroidNativeWindow; }
  ANativeWindow* window() const { return window_; }

 protected:
  bool GetSizeImpl(uint32_t& width_out, uint32_t& height_out) const override;

 private:
  ANativeWindow* window_;
};

}  // namespace rex::ui
```

Companion (platform-owned) requirements, restated:
- `window_sdl.cpp` Android branch: `CreateSurfaceImpl` → `AndroidNativeWindowSurface(...)`;
  `GetNativeWindowHandle` → the `ANativeWindow*` (today only Win32, `window_sdl.cpp:231-241`).
- Ensure `ANativeWindow_setBuffersGeometry(w, h, WINDOW_FORMAT_RGBA_8888)` before swapchain creation
  (SDL3 normally does it; the presenter does not).
- `GetSizeImpl` returns physical pixels (`ANativeWindow_getWidth/Height` or
  `SDL_GetWindowSizeInPixels`).

---

## P5. Not proposed (needs profiling first)

- **R1 (LOAD/STORE on every draw pass)** and **R2 (compute resolve)** are the real mobile costs, but
  changing load/store ops or pass splitting risks correctness (RT contents persist across passes).
  Instrument first (GPU trace + `vkCmd*` timings), then consider
  `DONT_CARE`/`CLEAR` only for provably fully-overwritten passes. No blind diff is offered.
- **R4 (scaled resolve buffer size):** the safe mitigation is configuration
  (`draw_resolution_scale_x/y = 1`), not a code change; a code change would need a non-sparse budget
  model that doesn't exist in the SDK yet.
