# Vulkan Compute — Development Plan (Linux GPU rendering)

> Companion pages: [Technical Index](_Index.md) · [Architecture Overview](Architecture-Overview.md) · [Cross-Platform Roadmap](CrossPlatform-Roadmap.md) · [Performance Development Plan](Performance-DevelopmentPlan.md) · [Deep-Zoom Perturbation](../Deep-Zoom-Perturbation.md)

> [!IMPORTANT]
> **Snapshot 2026-07-18.** Session decision: **keep the multi-renderer architecture** (DX stays
> primary on Windows, Silk GL + Skia CPU stay). Add a **Vulkan compute-only backend for
> Linux/macOS** to close the one real gap — GPU-computed fractal + colour on non-Windows hosts.
> This is a *forward-looking* plan, not shipped work. Nothing here touches the WinForms shell
> (deprecated) or the proven Windows DX path.

**Branch baseline:** `feature/ui-overhaul` (working) → cut `feature/vulkan-compute` from `main` for this work.
**Goal:** GPU-accelerated fractal iter/smooth + `EvalPalette` colour on **linux-x64 / linux-arm64**, reusing the existing HLSL kernel via **HLSL→SPIR-V (DXC)**. macOS via MoltenVK is a *stretch*, not a gate.
**Non-goal (this doc):** replacing DX on Windows; a WSI swapchain/present path in Vulkan; a GLSL emitter twin; touching the ILGPU calculator path.

---

## 1. Why Vulkan, why compute-only

Decided across the 2026-07-18 design session. Summary of the reasoning:

- **The gap is compute, not present.** Present is already a thin, CPU-buffer contract
  ([`IFractalRenderer.UpdateTexture(uint[] BGRA)`](../../Abstractions/IFractalRenderer.cs)). Only
  Windows (D3D11 HLSL compute) computes the fractal + colour on GPU; Silk GL and Skia are
  CPU-compute. Linux GPU rendering means **adding GPU compute**, not a new display path.
- **Shader reuse decides the API.** Vulkan consumes **SPIR-V**; **DXC compiles the existing HLSL
  kernel → SPIR-V**. The 856-line perturbation kernel + [`ColorGenHlslEmitter`](../../ColorGen/Emitters/ColorGenHlslEmitter.cs)
  survive largely intact. **This retires the previously-scoped GLSL-emitter twin.**
- **Compute-only skips Vulkan's worst boilerplate.** The current D3D kernel already round-trips
  through CPU (writes `gColor` structured buffer → `Map` → `UpdateTexture`). Mirror that:
  **Vulkan headless compute → map → existing Silk GL blit for present.** No swapchain, no WSI
  surface, no present-sync, no framebuffer. Drops into the current interface with zero present rewrite.
- **Native on Linux.** No MoltenVK translation pain (that only bites the macOS stretch goal).
  Silk.NET.Vulkan is same-vendor with the existing Silk GL usage — no new dependency ecosystem.

Runner-up rejected: **GL 4.3 compute** (reuses existing GL context, fastest to first-pixel) —
rejected because it forces a **permanent GLSL emitter + GLSL kernel port** to maintain in
parallel with HLSL. WebGPU rejected (WGSL = third shader lang, worse HLSL reuse). OpenCL/CUDA
rejected (declining / NVIDIA-only). Full detail in session notes.

---

## 2. Prior art & existing seams

| Asset | Where | Relevance |
|---|---|---|
| Present contract | [`IFractalRenderer`](../../Abstractions/IFractalRenderer.cs) | `UpdateTexture / Render / Resize`. Vulkan compute output feeds this unchanged. |
| Kernel interface | [`IGpuKernel`](../../Engine/Interefaces/IGpuKernel.cs) | Comments already name "future Metal/Vulkan backend" + "HLSL/SPIR-V-compatible palette". Designed for this. |
| Surface abstraction | [`IGpuSurface`](../../Abstractions/IGpuSurface.cs) | Exposes X11 Window XID "consumed by Vulkan/OpenGL". |
| Backend dispatch | [`RendererFactory.NonWin32Backend`](../../Engine/Rendering/RendererFactory.cs) | Open slot; `--renderer` flag already wired. Add a `Vulkan` enum member. |
| HLSL kernel | [`MandelbrotGpuKernel`](../../Rendering.D3D/MandelbrotGpuKernel.cs) | The CS source to compile → SPIR-V. `BuildHlsl(...)` composes cbuffer + IO + `EvalPalette`. |
| HLSL colour emit | [`ColorGenHlslEmitter`](../../ColorGen/Emitters/ColorGenHlslEmitter.cs) | Per-theme `float3 EvalPalette(...)`; reused as-is via DXC. |
| Present backend | [`SilkGLRenderer`](../../Rendering.Silk/SilkGLRenderer.cs) | Existing GL blit (`fColor = texture(uTex,vUv).bgra`); reused for present. |
| Smoke-gate pattern | [`Compute.Smoke`](../../Compute.Smoke/Program.cs), `Rendering.Silk.Smoke` | Exit 0/1/2; device enumerate → run kernel → histogram sanity. New Vulkan gate mirrors this. |
| Golden colour gate | `--colorprobe` (Program.cs) | Vulkan colour output must match the same embedded golden digest as CPU/HLSL. |

**Note — ILGPU coexists, is not replaced.** The ILGPU path (C# kernels → CUDA/OpenCL/CPU) drives
the fractal *calculators* + `Compute.Smoke`. This Vulkan work is the *renderer-side* HLSL compute
(iter/smooth + `EvalPalette`), a different kernel. See §7 open question O1 — the spike must decide
whether ILGPU could deliver Linux compute more cheaply before committing to Vulkan build-out.

---

## 3. Architecture

```
                 Windows                         Linux (new)
             ┌───────────────┐              ┌───────────────────┐
 calc/colour │ D3D11 HLSL CS │              │ Vulkan compute CS │  ← SPIR-V (DXC of same HLSL)
             │  → gColor buf │              │   → gColor buf    │
             └──────┬────────┘              └─────────┬─────────┘
                    │ Map(Read)                       │ vkMapMemory
                    ▼                                 ▼
             uint[] BGRA  ───────────────────►  uint[] BGRA
                    │        IFractalRenderer.UpdateTexture(...)
                    ▼                                 ▼
             DX present                        Silk GL blit (existing)
```

- **One HLSL source, two compilers.** D3DCompiler (Windows) and DXC→SPIR-V (Linux) both consume
  `BuildHlsl(...)` output. Guard the tiny dialect deltas (if any surface) behind a `#ifdef VULKAN`
  or a compile-time string swap in `BuildHlsl`.
- **Compute + present split by GPU queue, not process.** Vulkan owns compute; GL owns present.
  Interop is a CPU copy (the existing round-trip) — deliberately, to avoid Vulkan↔GL shared-memory
  extensions in v1. A zero-copy `VK_KHR_external_memory` present is a later optimisation (§7 O3).

---

## 4. Phased slices

Each slice lands behind a smoke gate and is independently revertible. Checkbox = acceptance.

| Slice | Issue |
|---|---|
| V0 spike | [#39](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/39) |
| V1 kernel | [#40](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/40) |
| V2 colour | [#41](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/41) |
| V3 backend/present | [#42](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/42) |
| V4 deep-zoom | [#43](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/43) |
| V5 macOS (stretch) | [#44](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/44) |

### V0 — Spike: HLSL→SPIR-V compute proof (headless, no UI)  ← [#39](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/39)  ✅ **DONE — see §9**
- [x] `dotnet` project `Rendering.Vulkan.Smoke` (mirrors `Compute.Smoke`): enumerate Vulkan
      devices, create instance/device/compute queue, no swapchain.
- [x] DXC toolchain wired: compile a **trivial** HLSL CS (`gColor[i] = pack(uv.x, uv.y, 0)`) → SPIR-V,
      load, dispatch 64×64, `vkMapMemory`, read back.
- [x] Histogram sanity like `Compute.Smoke` (≥3 distinct values; corners bit-exact vs CPU pack). Exit 0/1/2.
- [x] Runs on linux-x64 in CI under Mesa lavapipe (software Vulkan) so no GPU-in-CI needed.
- **Gate:** `Rendering.Vulkan.Smoke` exit 0 on lavapipe. **Answered O1/O2 — see §9.**

### V1 — Port the real kernel
- [ ] DXC-compile the full [`MandelbrotGpuKernel.BuildHlsl(null,null,emitColor:false)`](../../Rendering.D3D/MandelbrotGpuKernel.cs) base variant → SPIR-V.
- [ ] Wire cbuffer `Params` (b0) → Vulkan push-constants / UBO; `RWStructuredBuffer` u0–u3 →
      `VkBuffer` storage buffers; `StructuredBuffer` t0 (per-row) → storage buffer.
- [ ] iter/smooth output matches the D3D path for a fixed view (bit-exact or documented ULP band).
- **Gate:** new `--vulkanprobe` in Program.cs; iter/smooth digest vs golden (mirror `--colorprobe`).

### V2 — Colour pipeline (EvalPalette)
- [ ] DXC-compile the `emitColor:true` variant with a real theme's `HlslPaletteBody` + prelude.
- [ ] Per-theme SPIR-V compile-cache keyed on `PaletteId` (mirror the D3D `CompileShader` cache).
- **Gate:** `--colorprobe` passes on the Vulkan backend against the **same embedded golden digest**
  as CPU/HLSL. Colour parity is non-negotiable (see [user colourblindness constraint] — error UI
  aside, palette fidelity is the product).

### V3 — Renderer backend + present wiring
- [ ] `VulkanComputeRenderer : IFractalRenderer` — compute → map → hand buffer to `SilkGLRenderer` blit.
- [ ] `RendererBackend.Vulkan` enum member + `--renderer vulkan`; register into
      `RendererFactory.NonWin32Backend` from the Avalonia bootstrap on Linux.
- [ ] `ProbeDescription()` returns "Vulkan (compute) + OpenGL (present)" for the System Info dialog.
- **Gate:** interactive pan/zoom on a Linux host renders GPU-computed frames; falls back to Silk-CPU
  or Skia when no Vulkan device (lavapipe counts as present-but-slow — log it).

### V4 — Deep-zoom perturbation parity
- [ ] Reference-orbit + per-row `gPerRow` (t0) upload path validated deep (the 856-line kernel's
      real reason to exist). Re-run the deep-zoom gates: `--navrepro`, `--focusprobe`.
- **Gate:** deep-zoom golden views match D3D within the documented perturbation tolerance
      (read [Deep-Zoom-Perturbation.md](../Deep-Zoom-Perturbation.md) FIRST — do not re-derive).

### V5 (stretch) — macOS via MoltenVK
- [ ] Same SPIR-V through MoltenVK; validate the Vulkan-subset quirks. Gated `osx-arm64` CI leg.
- **Gate:** best-effort; CPU fallback stays the macOS default until this proves out.

---

## 5. Shader pipeline (HLSL → SPIR-V)

- **Compiler:** DXC (`dxc -T cs_6_0 -spirv`). Ship the DXC native per-RID, or invoke a NuGet-packaged
  DXC. Decide in V0 (O2).
- **Dialect deltas to watch:** `RWStructuredBuffer` register spaces → SPIR-V descriptor set/binding
  mapping (DXC `-fvk-*-binding` flags); `cbuffer` packing → std140/push-constant layout; row-major
  vs column-major (irrelevant here, no matrices); `fmod`/`frac` already normalised by the emitter.
- **No GLSL, ever.** The `ColorGenGlslEmitter` proposed in the prior session is **cancelled** by this
  approach — one fewer emitter to maintain.

---

## 6. Risks

| Risk | Mitigation |
|---|---|
| DXC HLSL→SPIR-V feature gaps in the perturbation kernel | V0 spike de-risks with a trivial kernel first; V1 ports incrementally with per-variant gates. |
| Vulkan boilerplate cost (solo dev) | Compute-only headless removes ~60% of it (no WSI/swapchain/present). Silk.NET.Vulkan gives thin bindings. |
| CI has no GPU | Mesa **lavapipe** (software Vulkan) runs the smoke gates headless; real-GPU validation is manual/local. |
| Colour drift on a new backend | `--colorprobe` golden gate is the hard stop; V2 cannot merge without it green. |
| Deep-zoom precision regression | V4 gated by existing `--navrepro`/`--focusprobe`; perturbation math is float/double same as HLSL. |
| Scope creep into Vulkan present | Explicit non-goal. Present stays GL. Zero-copy interop is a *later* optimisation, not v1. |

---

## 7. Open questions

- **O1 — ILGPU instead of Vulkan?** ILGPU already gives Linux GPU compute (OpenCL/CUDA/CPU) and is
  in-tree. Could the renderer's iter/smooth + a **C#** `EvalPalette` (via the existing
  [`ColorGenEmitter`](../../ColorGen/Emitters/ColorGenEmitter.cs), not HLSL) run on ILGPU and skip
  Vulkan entirely? **Trade:** ILGPU reuses the C# colour emitter but not the tuned HLSL kernel;
  Vulkan reuses the HLSL kernel but not the calculator infra. **V0 must A/B this before build-out.**
- **O2 — DXC delivery.** Native per-RID binary vs NuGet package vs build-time SPIR-V bake? Bake at
  build avoids shipping a runtime compiler but loses per-theme JIT (themes are dynamic). Likely:
  JIT the base kernel + per-theme SPIR-V at runtime like D3D does.
- **O3 — Zero-copy present.** `VK_KHR_external_memory` + GL `EXT_memory_object` to skip the CPU
  round-trip. Real speedup only if compute→CPU→GL becomes the bottleneck (it isn't today — compute
  dominates). Defer.
- **O4 — Vulkan compute + GL present on the same GPU/context** — confirm no driver conflict sharing
  the surface between a Vulkan compute queue and the GL present context on Mesa/NVIDIA Linux.

---

## 8. Further recommendations

See the session response / issue for the actionable list; folded into slices above where they gate
work (lavapipe CI, `--vulkanprobe`, colour-golden reuse, ILGPU A/B in V0).

---

## 9. V0 spike findings (issue [#39](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/39)) — **DONE**

Landed on branch `feature/vulkan-compute`. New standalone project `Rendering.Vulkan.Smoke`
(Silk.NET.Vulkan 2.23, mirrors `Compute.Smoke`, exit 0/1/2), in four commits: (1) instance +
device enumeration (`--list`) + logical device + compute queue, no WSI; (2) DXC HLSL→SPIR-V + full
compute pipeline, dispatch 64×64, `vkMapMemory` read-back + sanity; (3) lavapipe CI leg;
(4) findings. Plus two CI fixups (see below).

**Proven both ways.** Local (real GPU, GeForce GT 710): DXC-compiles the trivial uv-gradient kernel
→ dispatch → read-back `distinct=4096`, all four corners bit-exact vs the CPU pack → exit 0. CI
(software Vulkan, Mesa lavapipe on linux-x64): `Run Vulkan smoke` exits 0 —
[run 29682409585](https://github.com/AloneButUnsober/MandelbrotExplorer/actions/runs/29682409585).

The trivial kernel reuses the exact `cg_pack_bgra` convention from
[`MandelbrotGpuKernel`](../../Rendering.D3D/MandelbrotGpuKernel.cs), so V1 swaps in the real body
with no packing/endianness surprises.

### O1 — ILGPU vs Vulkan → **Vulkan** ✅
DXC→SPIR-V works end-to-end on the trivial kernel with **no dialect surprises**. That de-risks the
decisive advantage: the tuned 856-line perturbation kernel + `ColorGenHlslEmitter` survive **intact**
via DXC — zero re-port. The ILGPU alternative would re-express iter/smooth + `EvalPalette` as **C#**
kernels (`ColorGenEmitter`), reusing the C# colour emitter but abandoning the tuned HLSL kernel and
adding a second colour-parity surface. HLSL reuse is the higher-value asset; **proceed with Vulkan**.
ILGPU stays the calculator-side path, unchanged (§2 note holds).

### O2 — DXC delivery → **runtime JIT now, in-proc native later** ✅
V0 invokes the `dxc -spirv` **CLI at runtime** (located `DXC_PATH` → `VULKAN_SDK/Bin` → PATH),
mirroring D3D's runtime compile. Themes are dynamic, so a build-time SPIR-V bake **cannot** cover
per-theme `EvalPalette` variants. Recommendation for V2+: ship the **`dxcompiler` native per-RID and
call it in-proc** (matches in-proc D3DCompiler on Windows; drops the per-compile process spawn + the
CLI dependency); NuGet `Microsoft.Direct3D.DXC` is a viable managed-resolved source. Keep the CLI as
the dev/CI fallback. On CI, DXC ships fine from the LunarG `vulkan-sdk` apt package
(`/usr/bin/dxc`, libdxcompiler 1.8).

### Notes carried into V1
- **Binding map:** DXC needs **explicit `[[vk::binding(set,binding)]]`** — do not rely on the default
  u-register→binding mapping. V1 maps cbuffer `b0` → push-constants/UBO and `u0–u3` storage buffers
  with explicit `vk::binding`.
- **Memory:** V0 uses one `HOST_VISIBLE|HOST_COHERENT` storage buffer (direct map, no staging). A
  `DEVICE_LOCAL` + staging copy is a later perf option.
- **Parity band:** corners asserted bit-exact; interior pixels **not** (float ULP). V1's
  `--vulkanprobe` should adopt the documented ULP band, not strict bit-exact, for iter/smooth.

### CI caveat (not a Vulkan issue)
The Linux/macOS legs already fail at `Build FracturingFog.App` — it multi-targets
`net10.0;net10.0-windows`, and the CI step builds with no `-f`, so the `net10.0-windows` TFM pulls
the Windows-only `FracturingFog.Win` (WinForms) and trips NETSDK1073. Pre-existing on `main`; the
Vulkan steps are guarded `!cancelled()` so the gate still reports. Tracked in
[#49](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/49) (fix: build App `-f net10.0`
on non-Windows legs).
