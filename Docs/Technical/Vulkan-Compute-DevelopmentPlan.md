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

### V1 — Port the real kernel  ✅ **DONE — see §10**
- [x] DXC-compile the shared [`MandelbrotKernelSource.BuildBase()`](../../Rendering.D3D/MandelbrotKernelSource.cs) (the exact HLSL FXC compiles for the D3D base variant) → SPIR-V.
- [x] Wire cbuffer `Params` (b0) → UBO; `RWStructuredBuffer` u0–u2 (iter/smooth/finalZD) →
      storage buffers; `StructuredBuffer` t0 (per-row) → storage buffer. Bindings via DXC
      `-fvk-*-shift` (b→0, t→+100, u→+200) so the shared source needs no `vk::binding` attributes.
- [x] iter/smooth matches a C# float-mirror reference for a fixed view within a documented
      boundary-chaos band (see §10). Bit-exact is impossible cross-vendor (FMA/transcendental).
- **Gate:** `--vulkanprobe` in Program.cs; ULP-band parity vs the CPU reference (not a strict
  golden digest — set-boundary pixels are chaotic). Green on real GPU + lavapipe CI.

### V2 — Colour pipeline (EvalPalette) — **DONE** (see §11)
- [x] DXC-compile the `emitColor:true` variant with a real theme's `HlslPaletteBody` + prelude
      (Greyscale; `gColor` u3 → binding 203).
- [ ] Per-theme SPIR-V compile-cache keyed on `PaletteId` — **deferred to V3**: the probe compiles
      one theme per run, so a cache has no consumer until the renderer switches themes at runtime.
- **Gate:** `--colorprobe` passes on the Vulkan backend — embedded golden digest of a deterministic
  CPU mirror (reference stability) **+** GPU-vs-mirror byte-disagreement band (cross-vendor). Colour
  parity is non-negotiable (palette fidelity is the product). ✅ GT 710; lavapipe CI.

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
on non-Windows legs). **Fixed in [#50](https://github.com/AloneButUnsober/MandelbrotExplorer/pull/50).**

---

## 10. V1 findings (issue [#40](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/40)) — **DONE**

The real SP Mandelbrot base kernel (iter/smooth, no colour) now runs on Vulkan compute and matches
the D3D float math within a documented band.

**One source, two compilers — realised.** [`MandelbrotKernelSource`](../../Rendering.D3D/MandelbrotKernelSource.cs)
holds the cbuffer/IO header + `CSMain` body, extracted verbatim from `MandelbrotGpuKernel`. FXC
compiles it on Windows (unchanged D3D path); DXC compiles the *same string* to SPIR-V. The Vulkan
smoke `<Compile Include ... Link>`s that one dependency-free file — no Rendering.D3D/Engine
ProjectReference closure.

**Binding map (the O1/O2 §9 note, resolved).** The shared source carries **no `[[vk::binding]]`**
(those break FXC). DXC assigns bindings from register class via shift flags — `-fvk-b-shift 0 0`,
`-fvk-t-shift 100 0`, `-fvk-u-shift 200 0` — giving deterministic, collision-free bindings
(`Params` UBO → set0/binding0; `gPerRow` t0 → 100; `gIter/gSmooth/gFinalZD` u0..u2 → 200/201/202).
Without the shift, DXC starts each register class at binding 0 and `u0`/`t0` collide.

**cbuffer → UBO packing.** `Params` is 15 consecutive scalars (60 B) padded to a float4 multiple
(64 B). std140 packs scalars at 4-byte offsets with no interior padding, so the 64-byte C# blob maps
1:1 onto the DXC-generated UBO — the same bytes the D3D constant buffer uses.

**Parity result.** `--vulkanprobe` runs a fixed 128×128 shallow view and compares GPU iter/smooth
against a C# reference that mirrors the exact float ops. A pixel "agrees" when iter is equal and
(if escaped) smooth is within 0.05. Set-boundary pixels are chaotic — a 1-ULP FMA/transcendental
spread explodes the escape iteration there — so the gate bounds the **disagreement fraction** (≤1%)
+ in-set drift (≤1%), robust to that noise while a broken kernel (wrong bindings/endianness/math)
trips it at ~100%. Measured: **GeForce GT 710** in-set drift 0.012%, disagree 0.171% (4 iter + 24
smooth); **Mesa lavapipe** (CI) green. A strict golden digest was rejected — it can't survive
cross-vendor float without a false-fail.

### Notes carried into V2
- Colour splice points (`inSetColor`/`escapeColor`/`bulbSkipColor`) already parameterised in
  `MandelbrotKernelSource.HlslEntry`; V2 supplies them + the `EvalPalette`/`cg_pack_bgra` prelude
  (still in `MandelbrotGpuKernel.BuildHlsl` — extract to the shared source when V2 needs it).
- Per-theme SPIR-V compile-cache keyed on `PaletteId`, mirroring the D3D `_csByPaletteId` cache.
- `--colorprobe` colour-golden reuse is the V2 gate (colour parity is non-negotiable).

## 11. V2 findings (issue [#41](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/41)) — **DONE**

The colour-emitting kernel (`EvalPalette` → packed BGRA) now runs on Vulkan compute and matches a
deterministic CPU mirror of the same HLSL palette + pack within a documented band.

**Colour source extracted (the §10 carry-over, done).** The `gColor` UAV, ordered-dither
`cg_pack_bgra`, `EvalPalette` signature, and the three CSMain colour-write splices moved out of
`MandelbrotGpuKernel.BuildHlsl` into `MandelbrotKernelSource.{ColorPreludeHead/Tail, *ColorSplice,
BuildColor}`. Same "one source, two compilers" rule as V1 — FXC (D3D, unchanged; `BuildHlsl` now
delegates) and DXC (V2) consume the identical string. Still no `[[vk::binding]]`.

**Binding, extended.** The colour variant adds register class `u3` (`gColor`). The same
`-fvk-u-shift 200 0` puts it at binding 203; the probe wires a 6th descriptor (UBO@0,
SSBO@100/200/201/202/**203**). No new flag — the shift covers every `u#`.

**Gate — two parts, cross-vendor-robust (`--colorprobe`).** Colour parity is "non-negotiable" per
the plan, but a strict GPU digest can't survive cross-vendor float any more than V1's iter could.
So the gate splits the concern:
1. an **embedded golden digest** of a *deterministic CPU mirror* (the Greyscale `EvalPalette` body +
   `cg_pack_bgra`, dither off) — pins that the reference view/theme/pack itself did not drift; and
2. a **byte-disagreement band** of the GPU colour vs that mirror (±1/channel to absorb
   rounding-at-noise, ≤2% of pixels) — robust across GPUs. Boundary pixels flip black↔grey when the
   GPU iter and mirror iter disagree on in-set membership, exactly the V1 chaos, now in colour.

Plus a degenerate guard (<3 distinct colours ⇒ unbound `gColor`/collapsed pack) and an opaque-alpha
guard (every pixel's top byte must be 0xFF). The Greyscale theme was chosen because its C# `Map`
maps 1:1 to a short HLSL body using only `in_smooth`/`in_isInSet`, so the mirror is a faithful twin
with **no Engine ProjectReference** dragged into the standalone smoke project.

**Parity result.** `--colorprobe` on a fixed 128×128 view: **GeForce GT 710** distinct=132,
alphaBad=0, disagree 14/16384 (**0.085%**), exit 0. **Mesa lavapipe** (CI, LLVM 20.1.2) distinct=130,
alphaBad=0, disagree 24/16384 (**0.146%**), exit 0.

**Note on the golden's determinism — held.** The pinned digest is over the *CPU* mirror, not the GPU,
so it must be reproducible wherever the gate runs. `sqrt` is IEEE-correctly-rounded (safe cross-OS);
`MathF.Log`/`MathF.Sin` are not *guaranteed* bit-identical Windows↔Linux, but in practice the
lavapipe (Linux) CPU-mirror digest came back **byte-identical** to the Windows-regenerated golden
(`4e725d…f111`) — so the exact-digest half is safe here. If a future runtime/libm change ever
diverges across a rounding boundary, the band half stays green and `--colorprobe regen` on the
gate's own platform re-pins it.

### Notes carried into V3
- The colour kernel is proven headless; V3 wires `VulkanComputeRenderer : IFractalRenderer` (compute
  → map → hand buffer to the GL blit) and the `--renderer vulkan` selector.
- Per-theme SPIR-V caching (keyed on `PaletteId`) is still a V2-scoped nicety not yet built — the
  probe compiles one theme per run. Add the cache when the renderer switches themes at runtime.
