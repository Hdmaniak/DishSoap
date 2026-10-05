# ReXGlue SDK 0.10.0 — Xenos→Vulkan backend: mobile / TBDR (Adreno) audit

Scope: **read-only** audit of `<rexglue-sdk>` (SDK 0.10.0, Xenia/Xenos Vulkan
translation). Target device: **Samsung Galaxy A52 (SM-A525F), Qualcomm Adreno 618, Android 14
/ API 34, arm64-v8a**, driver build dated 2020‑11‑17.

Method:
1. Static enumeration of every Vulkan feature/extension/limit/format the backend requests
   (`src/ui/vulkan/`, `src/graphics/vulkan/`, `include/rex/graphics/vulkan/`).
2. A **Vulkan capability probe** cross‑compiled with NDK r28b and executed on the A52 in two forms
   — a native arm64 executable and a standalone APK module (`<android-vkprobe>/`).
   Both produced identical output. Raw captures: `probe-output-a52.txt` and
   `apk-probe-logcat-a52.txt`, reproduced in §8.
3. All non-measured statements are explicitly labelled **(speculation)**.

Legend: **REQUIRED** = hard `return nullptr` / init failure; **OPTIONAL** = enabled if present,
graceful fallback otherwise; **UNUSED** = listed because the task asked, but the backend has no
code path for it.

---

## 0. Headline results

- The A52 exposes **Vulkan 1.1.128** (instance 1.3.0), device `Adreno (TM) 618`, driver
  `Qualcomm Technologies Inc. Adreno Vulkan Driver` (id 8, conformance 1.2.0.1).
- The backend's **three hard feature requirements** — `independentBlend`,
  `fragmentStoresAndAtomics`, `vertexPipelineStoresAndAtomics` — are **all TRUE**, plus the two
  default‑required ones `geometryShader` and `fillModeNonSolid` are **TRUE**. `vkCreateDevice`
  with the exact required set succeeds (measured).
- On the measured A52 inputs, the **FBO/host‑render‑target path is the only viable path** and every
  one of its preconditions passes (formats, sample counts, bit‑exact transfers), so the
  **fragment‑shader‑interlock path is not needed** — and could not be used anyway because
  `VK_EXT_fragment_shader_interlock` is absent. There is **no hard capability blocker** for this
  title on Adreno 618. (Path selection itself is a runtime decision; the probe measured its inputs.)
- The dominant risks are **performance/bandwidth on the TBDR**, not correctness: the EDRAM emulation
  uses persistent host render targets with `loadOp=LOAD`/`storeOp=STORE` on every draw pass and a
  compute‑shader resolve‑to‑buffer, and render passes are broken frequently. This is the classic
  Xenos→mobile hotspot.
- Several optional extensions are **absent**: `VK_EXT_fragment_shader_interlock`,
  `VK_KHR_dynamic_rendering`, `VK_EXT_robustness2`, `VK_EXT_custom_border_color`,
  `VK_EXT_non_seamless_cube_map`, `VK_EXT_shader_stencil_export`, `VK_EXT_memory_budget`,
  `VK_EXT_shader_demote_to_helper_invocation`. Every one of them has a coded fallback in the SDK.

---

## 1. Vulkan extension inventory

### 1.1 Instance extensions

| Extension | Class | Evidence | A52 |
|---|---|---|---|
| `VK_KHR_surface` | REQUIRED when `with_surface` | `src/ui/vulkan/vulkan_instance.cpp:142` | ✔ |
| `VK_KHR_android_surface` | REQUIRED on Android when `with_surface` | `vulkan_instance.cpp:153-157` (`VK_USE_PLATFORM_ANDROID_KHR`, defined `include/rex/ui/vulkan/api.h:29-33`) | ✔ |
| `VK_KHR_get_physical_device_properties2` | Core ≥1.1, else requested | `vulkan_instance.cpp:126-133` | ✔ (as 1.1 core) |
| `VK_EXT_debug_utils` | OPTIONAL | `vulkan_instance.cpp:135` | ✘ (instance ext absent; no validation on device) |
| `VK_KHR_portability_enumeration` | OPTIONAL (Apple) | `vulkan_instance.cpp:138` | ✘ |
| `VK_KHR_xcb_surface` / `VK_KHR_wayland_surface` / `VK_KHR_win32_surface` / `VK_EXT_metal_surface` | other platforms | `vulkan_instance.cpp:143-167` | n/a |

Counts: **2 REQUIRED** (surface + android_surface), **1 required-on-<1.1**, **2 OPTIONAL** on
Android.

### 1.2 Device extensions

Requested by `src/ui/vulkan/vulkan_device.cpp:163-244`. "Promoted X.Y" means on a device whose
`apiVersion ≥ X.Y` the extension is **not** requested and the core feature/functionality is used
instead (`request_promoted_extension`, `vulkan_device.cpp:153-161`).

| Extension | Class | A52 (1.1) result |
|---|---|---|
| `VK_KHR_swapchain` | **REQUIRED** with swapchain (`:285-288`) | ✔ (rev 70) |
| `VK_KHR_dedicated_allocation` | REQUIRED for VMA usage (promoted 1.1) | ✔ (1.1 core) |
| `VK_KHR_get_memory_requirements2` | REQUIRED for VMA (promoted 1.1) | ✔ (1.1 core) |
| `VK_KHR_bind_memory2` | REQUIRED for VMA (promoted 1.1) | ✔ (1.1 core) |
| `VK_KHR_sampler_ycbcr_conversion` | OPTIONAL (promoted 1.1, used if present) | ✔ (1.1 core) |
| `VK_KHR_sampler_mirror_clamp_to_edge` | OPTIONAL (promoted 1.2) | ✔ (ext) |
| `VK_KHR_maintenance1` | OPTIONAL (promoted 1.1) | ✔ (1.1 core) |
| `VK_KHR_image_format_list` | OPTIONAL (promoted 1.2) | ✔ (ext) |
| `VK_KHR_driver_properties` | OPTIONAL (promoted 1.2) | ✔ (ext) |
| `VK_KHR_shader_float_controls` | OPTIONAL (promoted 1.2) | ✔ (ext) |
| `VK_KHR_spirv_1_4` | OPTIONAL (promoted 1.2, `api≥1.1`) | ✔ (ext) |
| `VK_EXT_shader_stencil_export` | OPTIONAL (FragStencilRef accuracy) | ✘ |
| `VK_EXT_fragment_shader_interlock` | OPTIONAL (FSI path only) | ✘ |
| `VK_KHR_dynamic_rendering` | OPTIONAL (promoted 1.3) | ✘ |
| `VK_KHR_maintenance4` | OPTIONAL (promoted 1.3) | ✘ |
| `VK_EXT_shader_demote_to_helper_invocation` | OPTIONAL (promoted 1.3) | ✘ |
| `VK_EXT_non_seamless_cube_map` | OPTIONAL | ✘ |
| `VK_EXT_custom_border_color` | OPTIONAL (YCbCr border parity) | ✘ |
| `VK_EXT_robustness2` | OPTIONAL (true null descriptors) | ✘ |
| `VK_EXT_memory_budget` | OPTIONAL | ✘ |
| `VK_KHR_portability_subset` | Apple only | n/a |

Counts: **1 REQUIRED** (`swapchain`) + 3 VMA‑required but promoted to 1.1 (satisfied by core);
**16 OPTIONAL**; **7 of the optionals are present, 9 absent**. **No absent optional extension is a
correctness blocker** (each has a fallback — see §2/§4).

Not used (verified absent from backend): `VK_KHR_push_descriptor`, `VK_KHR_descriptor_update_template`,
`VK_EXT_descriptor_indexing`, `VK_EXT_shader_object`, `VK_EXT_line_rasterization` (grep clean; the
`VK_EXT_shader_object` hit in `vulkan_device.cpp` is a comment only).

---

## 2. Feature requirement matrix

### 2.1 Hard requirements — `VulkanDevice::CreateIfSupported`
`src/ui/vulkan/vulkan_device.cpp:95-140`:

| Feature | Requirement | A52 | Consequence if false |
|---|---|---|---|
| `independentBlend` | **REQUIRED** (returns nullptr) `:96-111` | ✔ TRUE | hard fail |
| `fragmentStoresAndAtomics` | **REQUIRED** `:112-118` | ✔ TRUE | hard fail |
| `vertexPipelineStoresAndAtomics` | **REQUIRED** `:119-125` | ✔ TRUE | hard fail |
| `geometryShader` | default-required via cvar `vulkan_require_geometry_shader=!MAC` `:35,126-132` | ✔ TRUE | hard fail unless cvar off |
| `fillModeNonSolid` | default-required via cvar `vulkan_require_fill_mode_non_solid=!MAC` `:39,133-139` | ✔ TRUE | hard fail unless cvar off |

`vkCreateDevice` with the exact required set **and** with geometry+fillNonSolid added succeeded
(measured, probe `DEVICE_CREATE` lines).

### 2.2 Enabled if supported (OPTIONAL; correctness/perf impact if absent)
`vulkan_device.cpp:630-732` plus `render_target_cache.cpp`/`pipeline_cache.cpp` consumption:

| Feature / extension feature | A52 | Fallback in code |
|---|---|---|
| `robustBufferAccess` | ✔ | none needed |
| `fullDrawIndexUint32` | ✔ | primitive processor |
| `tessellationShader` | ✔ | — |
| `sampleRateShading` | ✔ | required for FSI only |
| `depthClamp` | ✔ | `pipeline_cache.cpp:1596` rejects clamped pipelines (parity loss) |
| `samplerAnisotropy` | ✔ | — |
| `occlusionQueryPrecise` | ✔ | — |
| `shaderClipDistance` / `shaderCullDistance` | ✔ / ✔ | `spirv_translator.cpp:53-54,1726-1740` |
| `sparseBinding` / `sparseResidencyBuffer` | ✘ | committed fallback `texture_cache.cpp:2122-2136` |
| `samplerMirrorClampToEdge` | ✔ | — |
| `uniformBufferStandardLayout`, `scalarBlockLayout` (1.2) | feature bits ✔ but **not enabled** on a 1.1 device (see §4 risk R6) | offset decorations emitted explicitly |
| `dynamicRendering` (1.3) | ✘ | classic `VkRenderPass` path (`command_processor.cpp:3095-3096`) |
| `shaderDemoteToHelperInvocation` | ✘ | `spirv_translator.cpp:2996-3004` variable/`OpKill` fallback |
| `fragmentShaderSampleInterlock`/`PixelInterlock` | ✘ | FBO path used instead |
| `nonSeamlessCubeMap` | ✘ | `texture_cache.cpp:839` flag skipped |
| `customBorderColors` | ✘ | `texture_cache.cpp:910-921` logged fallback |
| `nullDescriptor` (robustness2) | ✘ | `texture_cache.cpp:661,2960-2990` null-image fallback |
| `shaderStencilExport` | ✘ | `render_target_cache.cpp:4846` extra stencil-bit draws |
| `VK_EXT_memory_budget` | ✘ | allocations just work without budget |

Measured feature dump: probe output `FEATURES_1_0` (55 flags), `FEATURES_1_2`, `FEATURES_1_3`,
`FEATURES_EXT`.

### 2.3 Explicitly NOT used (desktop‑only features on many tilers — no risk here)
- **`wideLines` = false** on A52 — backend never sets `lineWidth != 1.0`
  (`pipeline_cache.cpp:3264`, `render_target_cache.cpp:4376`). No requirement.
- **`logicOp` = false** — no `logicOp`/`VkPipelineColorBlendStateCreateInfo::logicOpEnable` usage.
- **`largePoints` = true** — not needed as a feature (point size via `gl_PointSize`, which
  `shaderTessellationAndGeometryPointSize=false` would gate; the translator has a point‑size path).
- **`multiViewport` = false** — single‑viewport only.
- **`shaderFloat64` / `shaderInt64` = false** — no `OpCapability Int64/Float64` emitted by the
  SPIR‑V translator (grep). `shaderInt16` = true, `shaderStorageImageReadWithoutFormat` = true.

---

## 3. Format & sample‑count requirements (measured)

Backend checks in `src/graphics/vulkan/render_target_cache.cpp:241-415`. Required feature combos:
`SAMPLED_IMAGE | DEPTH_STENCIL_ATTACHMENT` (depth) and `SAMPLED_IMAGE | COLOR_ATTACHMENT` (color).

| Format | Backend check / use | A52 result |
|---|---|---|
| `VK_FORMAT_D24_UNORM_S8_UINT` | `render_target_cache.cpp:249-252`; `GetDepthVulkanFormat` `:1700-1705` | ✔ sampled+depth‑stencil → **used** |
| `VK_FORMAT_D32_SFLOAT_S8_UINT` | fallback depth | ✔ |
| `VK_FORMAT_R8G8B8A8_UNORM` | `k_8_8_8_8`, swapchain primary (`vulkan_presenter.cpp:1226`) | ✔ |
| `VK_FORMAT_A8B8G8R8_UNORM_PACK32` | `k_2_10_10_10` | ✔ |
| `VK_FORMAT_R16G16_SNORM` | `k_16_16` (`:1737-1739`) | ✔ |
| `VK_FORMAT_R16G16B16A16_SNORM` | `k_16_16_16_16` (`:1740-1742`) | ✔ |
| `VK_FORMAT_R16G16B16A16_UNORM` | gamma RT as UNORM16 (cvar default true, `flags.cpp:24`) | ✔ |
| `VK_FORMAT_R16G16_SFLOAT` / `R16G16B16A16_SFLOAT` | float RTs & fallbacks | ✔ |
| `VK_FORMAT_R16G16_UINT`, `R16G16B16A16_UINT`, `R32_UINT`, `R32G32_UINT` | ownership‑transfer formats (`:336-362`) | ✔ all sampled+color |
| Sample counts 1/2/4 | `framebuffer*`, `sampledImage*` | ✔ all `= 7` |

Consequences (measured, all pass):
- `depth_unorm24_vulkan_format_supported_ = true` → D24S8 used.
- `color_rg16/rgba16_draw_format_supported = true` → **no FSI fallback forced** (`:300-319`).
- `color_16bit/32bit_transfer_uint_formats_supported_ = true` and integer sample counts 1x/2x/4x
  present → `bit_exact_host_color_transfer_supported = true` (`:387-415`) → **FBO path retained**.
- `msaa_2x_attachments_supported_ = true`, `msaa_2x_no_attachments_supported_ = true`
  (`:372-386`; `native_2x_msaa` default true, `flags.cpp:18`).

Memory: heap 0 = **5494 MB** device‑local (UMA), heap 1 = 256 MB; memory type 2 is
`DEVICE_LOCAL|HOST_VISIBLE|HOST_COHERENT|HOST_CACHED`. `maxStorageBufferRange = 536870912`
(512 MB) exactly equals `SharedMemory::kBufferSize = 1<<29` (`include/rex/graphics/shared_memory.h:28-29`)
— the guest shared‑memory SSBO fits, but with **zero headroom** (see R7).

---

## 4. TBDR / mobile risk list

Severity is relative to *this* title (2D side‑scroller, 3D‑ish effects) on Adreno 618.

### R1 — EDRAM emulated as persistent host render targets with LOAD/STORE (HIGH, **performance**)
**Evidence:** `render_target_cache.cpp:1586-1628` — every generated render pass uses
`loadOp = VK_ATTACHMENT_LOAD_OP_LOAD`, `storeOp = VK_ATTACHMENT_STORE_OP_STORE` for depth and
color (stencil load/store `DONT_CARE` on color attachments only). `command_processor.cpp:3109-3160`
ends the current pass and begins a new one whenever the render‑pass key *or* framebuffer changes
(`:3104`, `:3178`). Dynamic rendering is off (`dynamicRendering=false`), so real
`vkCmdBeginRenderPass`/`vkCmdEndRenderPass` objects are used.
**Impact (speculation grounded in TBDR behaviour):** Adreno renders into GMEM tiles; a `LOAD` at
pass begin forces GMEM to be re‑fetched from system memory, and `STORE` writes it back. Because the
backend splits passes on RT key/framebuffer changes, a frame with many small passes pays that cost
repeatedly instead of keeping one tile resident. Expect elevated DDR/LPDDR bandwidth and GPU power.
Not a correctness issue. This is the single most important mobile concern.
**Mitigation (do NOT apply to the tree — see patches file):**
- For passes where the attachment is known to be fully overwritten by a clear/first draw, use
  `DONT_CARE`/`CLEAR`. The backend already does this for the swapchain present pass
  (`vulkan_presenter.cpp:903-905`) — the same idea is not applied to guest RTs because they persist.
  **(speculation)** A safe subset is the explicit resolve/clear transfers, which currently reuse the
  draw pass with `LOAD`.
- Reduce pass breaks: keep `current_render_pass_ == render_pass && current_framebuffer == framebuffer`
  alive across as many draws as possible (the code already early‑returns at
  `command_processor.cpp:3104`); investigate the transfer/resolve path forcing `EndRenderPass`.
- Consider capping/adjusting draw‑resolution scale to reduce bandwidth (§R4).
Good news: subpass dependencies already use `VK_DEPENDENCY_BY_REGION_BIT`
(`render_target_cache.cpp:1665,1672`), which is the TBDR‑friendly flag.

### R2 — EDRAM resolve is a compute copy to a 512 MB‑scale buffer (MEDIUM‑HIGH, **performance**)
**Evidence:** `render_target_cache.cpp` includes 26 resolve compute shaders (`:97-122`);
`DumpRenderTargets` / `TryResolveCopyDirectly` copy host‑RT contents via storage buffers into
`edram_buffer_` (`:5450-5922`, `:6069-6092`); `kDumpSamplesPerGroupX/Y = 8x16`
(`render_target_cache.h:676-677`) assumes a ≥128‑lane subgroup ("minimum required group size on
Vulkan, and the maximum number of lanes in a subgroup"). `maxPerStageDescriptorStorageBuffers` =
524288 on A52, so no binding‑count problem.
**Impact (speculation):** per‑frame resolve/dispatch cost plus full‑RT read/write bandwidth; the
8×16 workgroup mapping is safe (subgroup size 64/128 on Adreno) but is a fixed shape that may not
be optimal for Adreno's wave size.
**Note:** no `VK_EXT_fragment_shader_interlock`, so the direct‑from‑tile path cannot be used anyway.

### R3 — FSI fallback genuinely unavailable; FBO path must keep passing its gates (MEDIUM, **robustness**)
**Evidence:** A52 lacks `VK_EXT_fragment_shader_interlock` (and `fragmentShaderSampleInterlock`/
`PixelInterlock` are false). `render_target_cache.cpp:203-239` sets `fsi_path_supported=false`, so
`:300-319` / `:401-415` would `return false` (no GPU) if the FBO gates failed. They pass on this
device, so it works — but this is a fragile chain: any future Adreno driver that drops
`R16G16_SNORM`/`R16G16B16A16_SNORM` color attachment, integer‑MSAA sampled images, or UINT
transfer formats has **no fallback**. Keep `render_target_path_vulkan` unset/`fbo`; never set `fsi`.
**Evidence for the "absent" claim:** probe `HAS dext fragment_shader_interlock=false`.

### R4 — No sparse residency → scaled‑resolve buffer is fully committed (MEDIUM, **memory/OOM**)
**Evidence:** `texture_cache.cpp:2072-2140`: without `sparseResidencyBuffer` the buffer is created
with `VkBufferCreateFlags` 0 and one committed allocation;
`scaled_resolve_buffer_size_ = SharedMemory::kBufferSize(512 MB) * scale_x * scale_y` (`:2075-2076`).
It is only created when `IsDrawResolutionScaled()` (`:2336`). `draw_resolution_scale_x/y` default
**1** (`src/graphics/pipeline/texture/cache.cpp:66-70`), so at stock settings it is **not**
allocated. At 2×2 it would commit **2 GB** in a 5.5 GB UMA heap (measured), competing with guest
memory and other buffers → OOM/jank risk.
**Recommendation:** keep native resolution on mobile, or gate scale to 1 unless the buffer can be
allocated; `native_2x_msaa` (already true) is the cheaper AA lever.

### R5 — Optional extensions absent all have fallbacks, but each costs accuracy/perf (LOW–MEDIUM)
`robustness2`→null images (`texture_cache.cpp:2960`), `custom_border_color`→logged fallback
(`:910-921`), `non_seamless_cube_map`→flag skipped (`:839`), `shader_stencil_export`→extra
stencil‑bit draws (`render_target_cache.cpp:4846`), `demote_to_helper_invocation`→variable/`OpKill`
(`spirv_translator.cpp:2996-3004`), `dynamic_rendering`→classic render passes. These are
correct‑but‑slower/less‑exact paths. No action required for bring‑up; note for fidelity tuning.

### R6 — `scalarBlockLayout` / `uniformBufferStandardLayout` not enabled on a 1.1 device (LOW, **speculation**)
**Evidence:** `vulkan_device.cpp:339-343` links `VkPhysicalDeviceVulkan12Features` **only if
`apiVersion ≥ 1.2`**. The A52 reports apiVersion **1.1.128**, so those features are never enabled even
though the driver fills them TRUE in the 1.2 struct and exposes `VK_EXT_scalar_block_layout`. The
backend does not request the standalone `VK_EXT_scalar_block_layout` /
`VK_EXT_uniform_buffer_standard_layout` extensions as a fallback.
**Speculation:** if any translated SPIR‑V relies on scalar/std430 UBO layout, it could mismatch the
descriptor layout. Appears **not** to be the case: the translator emits explicit `DecorationOffset`
for its blocks (`spirv_translator.cpp:354,412,437,2834`; `render_target_cache.cpp:5444,5490-5493`)
and storage buffers default to std430, which needs no feature. Verify by running the real pipeline on
device; if descriptor‑layout validation errors appear, add the two extensions as fallbacks.

### R7 — `maxStorageBufferRange` exactly equals the 512 MB guest shared buffer (LOW, **brittle**)
Measured `maxStorageBufferRange = 536870912`, `SharedMemory::kBufferSize = 536870912`. The SSBO
fits exactly; any increase of `kBufferSize` (or a driver reporting a smaller range) would break
`vkBindBufferMemory`/descriptor updates. No action now; a guard/clamp would harden it.

### R8 — WSI/swapchain details (LOW)
`vulkan_presenter.cpp:1224-1291` prefers `VK_FORMAT_R8G8B8A8_UNORM` (secondary `B8G8R8A8_UNORM`) with
`VK_COLOR_SPACE_SRGB_NONLINEAR_KHR`; present modes tried IMMEDIATE→MAILBOX→FIFO_RELAXED→FIFO
(`:1384-1405`); `compositeAlpha` prefers OPAQUE then INHERIT (`:1362-1378`); transform prefers
IDENTITY then INHERIT (`:1355-1358`). Adreno's Android WSI reports R8G8B8A8/B8G8R8A8 and FIFO is
always available, so no blocker. `minImageCount = max(kSubmissionCount, minImageCount)` (`:1325`).
**(speculation)** IMMEDIATE/MAILBOX on Android can add latency/power cost; FIFO is the safe default
for thermal stability.

### R9 — Nothing desktop‑only in the shader path (INFO)
Capabilities emitted (grep of `spirv_translator*.cpp`): `Shader`, `Tessellation`,
`ClipDistance`/`CullDistance`, `SampleRateShading`, `DerivativeControl`,
`DenormFlushToZero`/`SignedZeroInfNanPreserve`/`RoundingModeRTE` (from `shader_float_controls`,
present), `FragmentShaderSample/PixelInterlockEXT` (FSI only), `DemoteToHelperInvocationEXT`
(only when supported; fallback exists). No `Int64`/`Float64`/`Geometry`‑only‑desktop capability.
`VK_EXT_shader_object` is **not** used. On this device the only capability that is added
conditionally and unavailable is demote, and the fallback is present.

---

## 5. Shader translation path

- Path: Xenos ucode → `src/graphics/pipeline/shader/spirv_translator*.cpp` →
  `SpirvBuilder`/glslang `spv` → `VkShaderModule`. glslang is vendored
  (`thirdparty/CMakeLists.txt:330-345`). SPIR‑V validation via `src/ui/vulkan/spirv_tools_context.cpp`
  (`:62-66` selects `SPV_ENV_VULKAN_1_1_SPIRV_1_4` for 0x10400). The device advertises
  `VK_KHR_spirv_1_4` (measured), so 1.4 output is legal.
- No geometry shaders are required by the *backend* unless a guest draw actually uses
  `kRectangleList`/`kQuadList` (`pipeline_cache.cpp:1446-1453`); `ArePipelineRequirementsMet`
  (`:1585-1588`) filters those if `geometryShader` is absent. On A52 it is present.
- **Conclusion:** generated SPIR‑V needs no desktop‑only capability for this title's likely
  feature set. (Speculation on "likely" — a shader dump + offline `spirv-val` with
  `SPV_ENV_VULKAN_1_1` would prove it; `dump_shaders` cvar exists, `flags.cpp:26`.)

---

## 6. Synchronization

- Barriers are batched/deferred (`command_processor.cpp:3080-3088 SubmitBarriers`), and render‑pass
  transitions are only emitted when the RT key/framebuffer changes (`:3104`,`:3178`). This is
  broadly TBDR‑friendly.
- Subpass dependencies use `VK_DEPENDENCY_BY_REGION_BIT` (`render_target_cache.cpp:1665,1672`) —
  correct and tile‑friendly.
- Render targets are tracked manually (`VulkanRenderTarget::current_stage_mask/access_mask/layout`,
  `render_target_cache.h:351-359`), so no hidden whole‑frame barriers were found.
- No correctness concern identified for TBDR. (Performance caveat is R1/R2, not sync.)

---

## 7. Android surface integration interface (for the platform agent)

The SDK already requires `include/rex/ui/surface_android.h` and class
`AndroidNativeWindowSurface` (`src/ui/vulkan/vulkan_presenter.cpp:37` include,
`:799-810` use). **Do not edit the platform agent's tree; this is the exact contract it must meet.**

### 7.1 What the presenter uses
```cpp
#if REX_PLATFORM_ANDROID
case Surface::kTypeIndex_AndroidNativeWindow: {
  auto& s = static_cast<const AndroidNativeWindowSurface&>(new_surface);
  VkAndroidSurfaceCreateInfoKHR ci{};
  ci.sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR;
  ci.window = s.window();                     // <-- must be ANativeWindow*
  vkCreateAndroidSurfaceKHR(instance, &ci, nullptr, &surf);
}
```
(`vulkan_presenter.cpp:799-810`.)

### 7.2 Required header content (`include/rex/ui/surface_android.h`)
```cpp
#pragma once
#include <rex/ui/surface.h>
#include <android/native_window.h>   // for ANativeWindow

namespace rex::ui {
class AndroidNativeWindowSurface final : public Surface {
 public:
  explicit AndroidNativeWindowSurface(ANativeWindow* window) : window_(window) {}
  TypeIndex GetType() const override { return kTypeIndex_AndroidNativeWindow; }
  ANativeWindow* window() const { return window_; }   // presenter:807
 protected:
  bool GetSizeImpl(uint32_t& width_out, uint32_t& height_out) const override;
 private:
  ANativeWindow* window_;
};
}  // namespace rex::ui
```
- Must derive from `rex::ui::Surface` (`include/rex/ui/surface.h:23`).
- `GetType()` must return `Surface::kTypeIndex_AndroidNativeWindow` (`surface.h:31`); the presenter
  matches `kTypeFlag_AndroidNativeWindow` (`vulkan_presenter.cpp:428`).
- `window()` return type must be `ANativeWindow*` (assigned to
  `VkAndroidSurfaceCreateInfoKHR::window`).
- `GetSizeImpl` must return the **physical‑pixel** window size (e.g. `ANativeWindow_getWidth/Height`
  or `SDL_GetWindowSizeInPixels`), and return `false`/zero until the window is valid
  (`surface.h:55-74`). The presenter clamps to Adreno's `maxFramebufferWidth/Height = 16384`
  (measured) and to `VkSurfaceCapabilitiesKHR`.
- The surface may be constructed on the UI thread and must outlive the present connection; the
  window must remain valid while the swapchain exists.

### 7.3 Additional obligations outside the header
1. `src/ui/window_sdl.cpp` must gain an Android branch in `CreateSurfaceImpl` returning
   `AndroidNativeWindowSurface(SDL_GetAndroidWindow(...))` / the SDL3 `ANativeWindow*`, and
   `GetNativeWindowHandle` must return it (currently only Win32, `window_sdl.cpp:231-241`).
   (The desktop `#else` currently includes `<X11/Xlib-xcb.h>` — the P0 blocker in
   `android-support-audit.md`.)
2. Before `vkCreateSwapchainKHR`, the `ANativeWindow` geometry/format must be set
   (`ANativeWindow_setBuffersGeometry(w, h, WINDOW_FORMAT_RGBA_8888)`); SDL3 normally does this, but
   if it does not, `surface_capabilities.currentExtent`/`maxImageExtent` will be wrong. The presenter
   itself never calls `ANativeWindow_setBuffersGeometry`.
3. Instance creation already requests `VK_KHR_surface` + `VK_KHR_android_surface`
   (`vulkan_instance.cpp:140-157`) and loads `vkCreateAndroidSurfaceKHR`
   (`include/rex/ui/vulkan/functions/instance_khr_android_surface.inc`), gated by
   `VK_USE_PLATFORM_ANDROID_KHR` (`api.h:29-33`). No changes needed there.
4. Swapchain format to expect on Adreno: `VK_FORMAT_R8G8B8A8_UNORM` (presenter primary on Android),
   `VK_COLOR_SPACE_SRGB_NONLINEAR_KHR`.
5. Sampling of `VK_KHR_android_surface` presence is via `GetSurfaceTypesSupportedByInstance`
   (`vulkan_presenter.cpp:420-431`); the Android type is only advertised when the instance extension
   is enabled, which the probe confirms is available.

---

## 8. Probe: build, run, and verbatim output

**Artifacts (all under `<android-vkprobe>/`):**

| Artifact | Path |
|---|---|
| Probe source (shared) | `vkprobe.c` |
| Native arm64 executable | `vkprobe` |
| Native capture (verbatim, 191 lines) | `probe-output-a52.txt` |
| **Probe APK** (separate Gradle module, own package `com.recomp.vkprobe`) | `app/build/outputs/apk/debug/app-debug.apk` |
| APK logcat capture (verbatim, 193 lines incl. tag prefix) | `apk-probe-logcat-a52.txt` |

Native build/run:
```
$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android34-clang \
    -O2 -Wall -o vkprobe vkprobe.c -lvulkan -llog
adb -s <device-serial> push vkprobe /data/local/tmp/vkprobe
adb -s <device-serial> shell /data/local/tmp/vkprobe
```
APK build/run:
```
cd <android-vkprobe> && ./gradlew --no-daemon assembleDebug
adb -s <device-serial> install -r app/build/outputs/apk/debug/app-debug.apk
adb -s <device-serial> shell am start -n com.recomp.vkprobe/.MainActivity
adb -s <device-serial> logcat -d -s VKPROBE:I > apk-probe-logcat-a52.txt
```

**Status: BOTH RAN SUCCESSFULLY.** The native executable exited 0 and the APK produced the
**identical** capability dump (same one physical device, same extensions/features/limits/formats,
`DEVICE_CREATE required-min OK`, `required+geometry+fillNonSolid OK`, `required+sparse FAIL (-8)`),
confirming the shell-UID and app-UID paths see the same `/system/lib64/libvulkan.so` →
`/vendor/lib64/hw/vulkan.adreno.so` stack. Measured device identity (SM-A525F, API 34, Adreno 618)
matches the target. The APK is a self-contained, installable project and does not touch
`<android-app>`.

Verbatim output (191 lines):

```
=== ReXGlue Vulkan capability probe ===
INSTANCE_EXTENSIONS 13
  iext VK_KHR_surface (rev 25)
  iext VK_KHR_surface_protected_capabilities (rev 1)
  iext VK_KHR_android_surface (rev 6)
  iext VK_EXT_swapchain_colorspace (rev 4)
  iext VK_KHR_get_surface_capabilities2 (rev 1)
  iext VK_GOOGLE_surfaceless_query (rev 2)
  iext VK_EXT_surface_maintenance1 (rev 1)
  iext VK_EXT_debug_report (rev 10)
  iext VK_KHR_get_physical_device_properties2 (rev 1)
  iext VK_KHR_external_semaphore_capabilities (rev 1)
  iext VK_KHR_external_memory_capabilities (rev 1)
  iext VK_KHR_device_group_creation (rev 1)
  iext VK_KHR_external_fence_capabilities (rev 1)
INSTANCE_VERSION 1.3.0
PHYSICAL_DEVICES 1
--- device 0 ---
DEVICE name='Adreno (TM) 618' type=1 apiVersion=1.1.128 driverVersion=0x801F6000 vendorID=0x5143 deviceID=0x6010800
DEVICE_EXTENSIONS 57
  dext VK_KHR_incremental_present (rev 2)
  dext VK_EXT_hdr_metadata (rev 2)
  dext VK_KHR_shared_presentable_image (rev 1)
  dext VK_GOOGLE_display_timing (rev 1)
  dext VK_EXT_swapchain_maintenance1 (rev 1)
  dext VK_EXT_subgroup_size_control (rev 2)
  dext VK_KHR_external_memory (rev 1)
  dext VK_EXT_pipeline_creation_feedback (rev 1)
  dext VK_KHR_shader_float16_int8 (rev 1)
  dext VK_KHR_get_memory_requirements2 (rev 1)
  dext VK_KHR_spirv_1_4 (rev 1)
  dext VK_KHR_external_semaphore_fd (rev 1)
  dext VK_QCOM_render_pass_store_ops (rev 2)
  dext VK_KHR_external_memory_fd (rev 1)
  dext VK_QCOM_render_pass_shader_resolve (rev 4)
  dext VK_KHR_maintenance1 (rev 1)
  dext VK_KHR_maintenance2 (rev 1)
  dext VK_KHR_maintenance3 (rev 1)
  dext VK_KHR_separate_depth_stencil_layouts (rev 1)
  dext VK_KHR_buffer_device_address (rev 1)
  dext VK_EXT_queue_family_foreign (rev 1)
  dext VK_KHR_bind_memory2 (rev 1)
  dext VK_KHR_external_semaphore (rev 1)
  dext VK_EXT_scalar_block_layout (rev 1)
  dext VK_KHR_sampler_ycbcr_conversion (rev 1)
  dext VK_EXT_vertex_attribute_divisor (rev 3)
  dext VK_KHR_variable_pointers (rev 1)
  dext VK_KHR_push_descriptor (rev 1)
  dext VK_EXT_device_memory_report (rev 1)
  dext VK_KHR_device_group (rev 2)
  dext VK_KHR_relaxed_block_layout (rev 1)
  dext VK_KHR_external_fence (rev 1)
  dext VK_EXT_host_query_reset (rev 1)
  dext VK_EXT_index_type_uint8 (rev 1)
  dext VK_KHR_multiview (rev 1)
  dext VK_KHR_storage_buffer_storage_class (rev 1)
  dext VK_KHR_shader_subgroup_extended_types (rev 1)
  dext VK_EXT_pipeline_creation_cache_control (rev 1)
  dext VK_EXT_separate_stencil_usage (rev 1)
  dext VK_KHR_image_format_list (rev 1)
  dext VK_EXT_sampler_filter_minmax (rev 1)
  dext VK_KHR_create_renderpass2 (rev 1)
  dext VK_KHR_shader_float_controls (rev 4)
  dext VK_EXT_texture_compression_astc_hdr (rev 1)
  dext VK_EXT_global_priority (rev 2)
  dext VK_KHR_shader_draw_parameters (rev 1)
  dext VK_KHR_vulkan_memory_model (rev 3)
  dext VK_EXT_line_rasterization (rev 1)
  dext VK_KHR_descriptor_update_template (rev 1)
  dext VK_KHR_draw_indirect_count (rev 1)
  dext VK_KHR_driver_properties (rev 1)
  dext VK_KHR_uniform_buffer_standard_layout (rev 1)
  dext VK_ANDROID_external_memory_android_hardware_buffer (rev 3)
  dext VK_KHR_dedicated_allocation (rev 1)
  dext VK_KHR_swapchain (rev 70)
  dext VK_KHR_sampler_mirror_clamp_to_edge (rev 1)
  dext VK_KHR_external_fence_fd (rev 1)
HAS dext fragment_shader_interlock=false robustness2=false custom_border_color=false non_seamless_cube_map=false memory_budget=false shader_stencil_export=false shader_demote_to_helper_invocation=false dynamic_rendering=false spirv_1_4=TRUE drivers=TRUE maintenance4=false image_format_list=TRUE sampler_ycbcr=TRUE shader_float_controls=TRUE dedicated_allocation=TRUE
FEATURES_1_0:
FEATURE robustBufferAccess                         TRUE
FEATURE fullDrawIndexUint32                        TRUE
FEATURE imageCubeArray                             TRUE
FEATURE independentBlend                           TRUE
FEATURE geometryShader                             TRUE
FEATURE tessellationShader                         TRUE
FEATURE sampleRateShading                          TRUE
FEATURE dualSrcBlend                               TRUE
FEATURE logicOp                                    false
FEATURE multiDrawIndirect                          TRUE
FEATURE drawIndirectFirstInstance                  TRUE
FEATURE depthClamp                                 TRUE
FEATURE depthBiasClamp                             TRUE
FEATURE fillModeNonSolid                           TRUE
FEATURE depthBounds                                TRUE
FEATURE wideLines                                  false
FEATURE largePoints                                TRUE
FEATURE alphaToOne                                 TRUE
FEATURE multiViewport                              false
FEATURE samplerAnisotropy                          TRUE
FEATURE textureCompressionETC2                     TRUE
FEATURE textureCompressionASTC_LDR                 TRUE
FEATURE textureCompressionBC                       false
FEATURE occlusionQueryPrecise                      TRUE
FEATURE pipelineStatisticsQuery                    TRUE
FEATURE vertexPipelineStoresAndAtomics             TRUE
FEATURE fragmentStoresAndAtomics                   TRUE
FEATURE shaderTessellationAndGeometryPointSize     false
FEATURE shaderImageGatherExtended                  TRUE
FEATURE shaderStorageImageExtendedFormats          TRUE
FEATURE shaderStorageImageMultisample              false
FEATURE shaderStorageImageReadWithoutFormat        TRUE
FEATURE shaderStorageImageWriteWithoutFormat       TRUE
FEATURE shaderUniformBufferArrayDynamicIndexing    TRUE
FEATURE shaderSampledImageArrayDynamicIndexing     TRUE
FEATURE shaderStorageBufferArrayDynamicIndexing    TRUE
FEATURE shaderStorageImageArrayDynamicIndexing     TRUE
FEATURE shaderClipDistance                         TRUE
FEATURE shaderCullDistance                         TRUE
FEATURE shaderFloat64                              false
FEATURE shaderInt64                                false
FEATURE shaderInt16                                TRUE
FEATURE shaderResourceResidency                    false
FEATURE shaderResourceMinLod                       false
FEATURE sparseBinding                              false
FEATURE sparseResidencyBuffer                      false
FEATURE sparseResidencyImage2D                     false
FEATURE sparseResidencyImage3D                     false
FEATURE sparseResidency2Samples                    false
FEATURE sparseResidency4Samples                    false
FEATURE sparseResidency8Samples                    false
FEATURE sparseResidency16Samples                   false
FEATURE sparseResidencyAliased                     false
FEATURE variableMultisampleRate                    false
FEATURE inheritedQueries                           TRUE
FEATURES_1_2 samplerMirrorClampToEdge=TRUE uniformBufferStandardLayout=TRUE scalarBlockLayout=TRUE descriptorIndexing=false shaderSubgroupExtendedTypes=TRUE hostQueryReset=TRUE timelineSemaphore=false bufferDeviceAddress=TRUE
FEATURES_1_3 shaderDemoteToHelperInvocation=false dynamicRendering=false maintenance4=false subgroupSizeControl=false
FEATURES_EXT fragmentShaderSampleInterlock=false fragmentShaderPixelInterlock=false fragmentShaderShadingRateInterlock=false
FEATURES_EXT robustness2 nullDescriptor=false
FEATURES_EXT customBorderColors=false customBorderColorWithoutFormat=false
FEATURES_EXT nonSeamlessCubeMap=false
DRIVER id=8 name='Qualcomm Technologies Inc. Adreno Vulkan Driver' info='Driver Build: e1ac91e, I2b3b5fbd00, 1605635143
Date: 11/17/20
Compiler Version: EV031.32.02.06
Driver Branch: 
' conformance=1.2.0.1
LIMITS maxImageDimension2D=16384 maxImageDimension3D=2048 maxImageArrayLayers=2048
LIMITS maxStorageBufferRange=536870912 maxSamplerAllocationCount=4000
LIMITS maxPerStageDescriptorSamplers=524288 maxPerStageDescriptorStorageBuffers=524288 maxPerStageDescriptorSampledImages=524288 maxPerStageResources=1572864
LIMITS maxVertexOutputComponents=128 maxFragmentInputComponents=112 maxFragmentCombinedOutputResources=72
LIMITS maxGeometryInputComponents=128 maxGeometryOutputComponents=128 maxTessellationEvaluationOutputComponents=128
LIMITS maxSamplerAnisotropy=16.00 maxViewportDimensions=16384x16384
LIMITS minUniformBufferOffsetAlignment=64 minStorageBufferOffsetAlignment=64 nonCoherentAtomSize=1
LIMITS framebufferWidth=16384 framebufferHeight=16384 maxFramebufferWidth=16384 maxFramebufferHeight=16384
LIMITS maxDrawIndirectCount=4294967295 maxVertexInputAttributes=32 maxVertexInputBindings=32
SAMPLECOUNTS framebufferColor=7 framebufferDepth=7 framebufferStencil=7 framebufferNoAttachments=7 sampledColor=7 sampledInteger=7 sampledDepth=7 sampledStencil=7
LIMITS standardSampleLocations=TRUE timestampPeriod=52.08
FORMAT D24_UNORM_S8_UINT                  opt=0x0001D601 linear=0x0001D401 buffer=0x00000000
FORMAT D32_SFLOAT_S8_UINT                 opt=0x0001C601 linear=0x0001C401 buffer=0x00000000
FORMAT D16_UNORM                          opt=0x0001C601 linear=0x0001C401 buffer=0x00000000
FORMAT D32_SFLOAT                         opt=0x0001C601 linear=0x0001C401 buffer=0x00000000
FORMAT R8G8B8A8_UNORM                     opt=0x0007DD83 linear=0x0007DD83 buffer=0x00000058
FORMAT R8G8B8A8_SRGB                      opt=0x0001DD81 linear=0x0001DD81 buffer=0x00000000
FORMAT A8B8G8R8_UNORM_PACK32              opt=0x0007DD83 linear=0x0007DD83 buffer=0x00000058
FORMAT B8G8R8A8_UNORM                     opt=0x0001DD81 linear=0x0001DD81 buffer=0x00000048
FORMAT R16G16_UNORM                       opt=0x0001DD83 linear=0x0001DD83 buffer=0x00000058
FORMAT R16G16_SNORM                       opt=0x0001DD83 linear=0x0001DD83 buffer=0x00000058
FORMAT R16G16B16A16_SNORM                 opt=0x0001DD83 linear=0x0001DD83 buffer=0x00000058
FORMAT R16G16B16A16_UNORM                 opt=0x0001DD83 linear=0x0001DD83 buffer=0x00000058
FORMAT R16G16_SFLOAT                      opt=0x0001DD83 linear=0x0001DD83 buffer=0x00000058
FORMAT R16G16B16A16_SFLOAT                opt=0x0001DD83 linear=0x0001DD83 buffer=0x00000058
FORMAT R32_SFLOAT                         opt=0x0001CD83 linear=0x0001CD83 buffer=0x00000058
FORMAT R32G32_SFLOAT                      opt=0x0001CD83 linear=0x0001CD83 buffer=0x00000058
FORMAT R16_UINT                           opt=0x0001CC83 linear=0x0001CC83 buffer=0x00000058
FORMAT R16G16_UINT                        opt=0x0001CC87 linear=0x0001CC87 buffer=0x00000078
FORMAT R16G16B16A16_UINT                  opt=0x0001CC83 linear=0x0001CC83 buffer=0x00000058
FORMAT R32_UINT                           opt=0x0001CC87 linear=0x0001CC87 buffer=0x00000078
FORMAT R32G32_UINT                        opt=0x0001CC83 linear=0x0001CC83 buffer=0x00000058
FORMAT R32G32B32A32_UINT                  opt=0x0001CC83 linear=0x0001CC83 buffer=0x00000058
MEMORY heaps=2 types=6
  heap 0 size=5494MB flags=0x1
  heap 1 size=256MB flags=0x1
  memtype 0 flags=0x1 heap=0
  memtype 1 flags=0xB heap=0
  memtype 2 flags=0xF heap=0
  memtype 3 flags=0x1 heap=0
  memtype 4 flags=0x7 heap=0
  memtype 5 flags=0x21 heap=1
DEVICE_CREATE required-min OK (0)
DEVICE_CREATE required+geometry+fillNonSolid OK (0)
DEVICE_CREATE required+sparse FAIL (-8)
=== probe done ===
```

Format mask decode (SAMPLED_IMAGE=0x1, COLOR_ATTACHMENT=0x80, DEPTH_STENCIL_ATTACHMENT=0x200):
`D24_UNORM_S8_UINT` has both sampled + depth‑stencil; all `R16G16*`/`R16G16B16A16*` and UINT
transfer formats have both sampled + color‑attachment; `sampledInteger = 7` (1x/2x/4x).

---

## 9. Labeled speculation (not measured)

- **(speculation)** R1's bandwidth cost magnitude: no GPU counter/trace was captured, so the claim
  "LOAD/STORE + frequent pass breaks is the dominant cost" is grounded in the code and TBDR
  architecture but not measured. A future step is a GPU trace (Adreno Profiler / `simpleperf` +
  vendor counters) on the real title.
- **(speculation)** R2's subgroup‑shape efficiency (8×16 workgroups) on Adreno's wave size.
- **(speculation)** R6 (scalar/uniform std layout) — believed harmless because offsets are explicit;
  not validated by running a real pipeline.
- **(speculation)** R8 (IMMEDIATE/MAILBOX latency/power on Android).
- **(speculation)** `vulkan_require_geometry_shader`/`fillModeNonSolid` being irrelevant because the
  title may not use rectangle/quad or wireframe primitives; both features are TRUE anyway.

---

## 10. Recommended actions (audit conclusion)

1. **Do not set `render_target_path_vulkan=fsi`** — the FSI path cannot work on Adreno 618. Leave it
   at the default (FBO).
2. **Keep `draw_resolution_scale_x/y = 1`** on mobile (avoid the 2 GB committed resolve buffer, R4).
   Use `native_2x_msaa=true` (default) for AA instead.
3. **Bring-up configuration** is otherwise safe: all hard features and format gates pass on the A52;
   no Android‑specific Vulkan blocker beyond the missing `surface_android.h` (§7).
4. **Performance work item (post‑bring‑up):** instrument R1/R2 before changing anything. If
   bandwidth‑bound, the highest‑value targeted changes are (a) reduce render‑pass breaks around
   transfers/resolves, (b) `DONT_CARE`/`CLEAR` where an attachment is provably fully overwritten.
5. Provide the platform agent the interface in §7.2 exactly; the only SDK‑side consumer is
   `vulkan_presenter.cpp:799-810`.
