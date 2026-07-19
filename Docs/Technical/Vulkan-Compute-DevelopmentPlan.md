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
  kernel → SPIR-V**. The shallow escape-time kernel (`MandelbrotKernelSource`) + colour emitter
  ([`ColorGenHlslEmitter`](../../ColorGen/Emitters/ColorGenHlslEmitter.cs)) survive largely intact.
  **This retires the previously-scoped GLSL-emitter twin.**
  > **Correction (2026-07-19, V4):** this bullet originally read "the 856-line perturbation kernel
  > … survive largely intact." **There is no HLSL perturbation kernel** — the HLSL path is a ~290-line
  > FP32 escape-time kernel used only at `Zoom ≤ MaxGpuZoom (1e4)`; deep-zoom perturbation lives on
  > the CPU. The reuse argument holds for the *shallow* kernel + colour pipeline (what V1–V3 shipped);
  > a GPU perturbation kernel is net-new, tracked in [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82). See §13.
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
| V4 deep-zoom (parity — resolved trivially, §13) | [#43](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/43) |
| V6 GPU perturbation kernel (net-new; **spike CLEARED §14; full build LANDED Vulkan §15**, D3D + GUI-enable fast-follow) | [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82) |
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

### V3 — Renderer backend + present wiring — **DONE (headless half; see §12)**
- [x] Vulkan compute at the **`IGpuKernel`** boundary — `VulkanComputeKernel` (Rendering.Vulkan lib),
      the same boundary the D3D kernel uses. (Design note: `IFractalRenderer` can't compute — it
      receives a finished buffer — so "Vulkan compute" plugs in as an `IGpuKernel`, present stays
      `SilkGLRenderer`. Chosen over a `VulkanComputeRenderer : IFractalRenderer` composite.)
- [x] `RendererBackend.Vulkan` enum member + `ProbeDescription()` composite string + `Create()`
      present-via-GL routing (scaffolding). — CLI `--renderer vulkan` parse + bootstrap
      kernel-injection into the calculator **deferred to the V3 GUI follow-up**.
- [x] `ProbeDescription()` returns "Vulkan (compute) + OpenGL (present)".
- [ ] Live interactive pan/zoom on a Linux host + no-device fallback — **V3 GUI follow-up** (needs a
      display; the headless `--vulkanrenderprobe` gate proves the kernel end-to-end first).
- **Gate:** `--vulkanrenderprobe` (headless) drives the real kernel — colour + base parity, buffer
  persistence, resize realloc. ✅ GT 710; lavapipe CI. Interactive gate is the follow-up.

### V4 — Deep-zoom perturbation parity — **RESOLVED: parity holds trivially (see §13)**
- [x] Confirmed the deep-zoom render path is **backend-independent**, so Vulkan selection cannot
      regress it, and the `--focusprobe` gate is green on HEAD (0.00 px focus error to 1e60).
- **Correction (2026-07-19):** the premise below was wrong — **no HLSL perturbation kernel exists.**
  The HLSL compute kernel (`MandelbrotKernelSource`) is a shallow FP32 split-centre escape-time
  kernel gated at `MandelbrotCalculator.MaxGpuZoom = 1e4`; above that the calculator never dispatches
  a `GpuKernel` at all — deep zoom is **all-CPU** (SIMD perturbation + DD/QD/OD reference orbit;
  `MandelbrotRefOrbitGpu` is an ILGPU *reference-orbit* helper, per-pixel δ stays on CPU). D3D and
  Vulkan therefore compute identical deep-zoom frames because neither touches the GPU past 1e4.
  `--focusprobe`/`--navrepro` are headless **calculator** self-tests that run before any `--renderer`
  parse, so "re-run them on the Vulkan backend" was never meaningful. A **real** GPU perturbation
  kernel (ref-orbit SSBO + per-pixel δ + rebasing in-shader) is genuine net-new work that *no* backend
  has today — re-filed as its own spike-gated slice, [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82).
- **Gate (as-shipped):** `--focusprobe` green on HEAD; deep-zoom output invariant under `--renderer`
  selection by construction (`Zoom > MaxGpuZoom` ⇒ no GPU dispatch).

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
| DXC HLSL→SPIR-V feature gaps in the ~~perturbation~~ *shallow* kernel | V0 spike de-risks with a trivial kernel first; V1 ports incrementally with per-variant gates. (The GPU *perturbation* kernel's feared DXC risk — QD/DD limb math in HLSL — was **retired by the [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82) spike, §14**: the default δ path is plain `double`, no limbs; DXC `cs_6_0` + FXC `cs_5_0` both compile it, GT710 runs it. Needs `shaderFloat64`.) |
| Vulkan boilerplate cost (solo dev) | Compute-only headless removes ~60% of it (no WSI/swapchain/present). Silk.NET.Vulkan gives thin bindings. |
| CI has no GPU | Mesa **lavapipe** (software Vulkan) runs the smoke gates headless; real-GPU validation is manual/local. |
| Colour drift on a new backend | `--colorprobe` golden gate is the hard stop; V2 cannot merge without it green. |
| Deep-zoom precision regression | **N/A for V1–V5** — deep zoom is all-CPU (`Zoom > MaxGpuZoom 1e4` ⇒ no GPU dispatch), so no backend can regress it; `--focusprobe` green on HEAD confirms. Becomes a live risk only once [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82) puts perturbation on the GPU. |
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
decisive advantage: the tuned HLSL escape-time kernel + `ColorGenHlslEmitter` survive **intact**
via DXC — zero re-port. _(2026-07-19 correction: this originally said "856-line perturbation kernel";
that kernel does not exist — the reused HLSL is the ~290-line shallow escape-time + colour kernel. See
§13 / [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82).)_ The ILGPU alternative would re-express iter/smooth + `EvalPalette` as **C#**
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

## 12. V3 findings (issue [#42](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/42)) — **DONE (headless half)**

Vulkan compute is now a real, referenced backend at the `IGpuKernel` boundary, driven through the
exact API the calculator uses, and gated headless. The interactive GL-present wiring is a follow-up.

**Interface choice — `IGpuKernel`, not `IFractalRenderer`.** The issue framed a
`VulkanComputeRenderer : IFractalRenderer` that *computes*, but `IFractalRenderer.UpdateTexture`
receives an already-finished BGRA buffer and has no fractal params — a renderer can't compute the
frame. The compute boundary is `IGpuKernel` (exactly where the D3D `MandelbrotGpuKernel` lives), so
`VulkanComputeKernel : IGpuKernel` is the faithful fit; present stays `SilkGLRenderer`. The
"renderer" is a thin selector: `RendererBackend.Vulkan` + a composite probe string, with present
routed through the same `NonWin32Backend` GL blit (Vulkan is compute-only, no swapchain).

**Library promote.** The plumbing (`VulkanContext`, `DxcCompiler`) moved out of the standalone
`Rendering.Vulkan.Smoke` gate into a referenced cross-platform **`Rendering.Vulkan`** library
(net10.0, refs Engine + Abstractions, Silk.NET.Vulkan) — mirroring how `Rendering.D3D` hosts the D3D
kernel. The smoke project is now the gate harness *over* that library.

**`VulkanComputeKernel` design.** Persistent device objects: a base (no-colour) pipeline + per-
`PaletteId` colour pipelines (`SetPalette` compiles + caches via `BuildColor`, mirroring the D3D
`_csByPaletteId` cache — so V2's deferred per-theme cache is delivered here); HOST_VISIBLE buffers
re-allocated only on a dimension change; one command pool. **Per-Run** it creates and destroys a
small descriptor pool + set + command buffer, so a mid-session resize can never leave a descriptor
bound to a freed buffer. `Run()` fills iter/smooth/finalZD (+ optional packed BGRA) exactly like the
D3D kernel, including the split hi/lo centre+scale and the `GradientColorMap` dither knob for byte
parity. DXC `-fvk-*-shift` binding maps unchanged (`u3`/`gColor` → 203).

**Gate — `--vulkanrenderprobe`.** Drives the production kernel object (not a hand-rolled dispatch)
and checks four things the renderer relies on: (1) colour Run parity vs the CPU mirror; (2) base Run
iter/smooth parity; (3) buffer **persistence** — a repeat identical Run is byte-for-byte identical;
(4) **resize** — a Run at a different W×H re-allocates and still matches a CPU reference. Uses the
real Engine `GrayscalePalette`, reached transitively through the `Rendering.Vulkan → Engine`
reference. Measured **GeForce GT 710**: frameA colour 0.085% / iter 0.171%, persistence identical,
resize 96×160 colour 0.052% / iter 0.156%, base 0.171%, exit 0. **Mesa lavapipe** (CI, LLVM 20.1.2):
frameA colour 0.146% / iter 0.269%, persistence identical, resize 0.078% / 0.221%, base 0.269%,
exit 0.

**Build note.** The legacy WinExe (`FracturingFogCLD.csproj`) globs the repo root and `<Compile
Remove>`s each sibling project; the new `Rendering.Vulkan\**` folder needed its own exclude (same
class as the V2 `Rendering.Vulkan.Smoke` fix). Any new sibling net10 project needs one.

### Notes carried into the V3 GUI follow-up
- Parse `--renderer vulkan` in `Program.cs` → `RendererFactory.PreferredBackend = Vulkan`.
- In the Avalonia/host bootstrap: construct one `VulkanContext` + `VulkanComputeKernel`, attach it to
  the calculator (`calc.GpuKernel = kernel; calc.UseGpuCompute = true`) when Vulkan is selected, and
  set `RendererFactory.VulkanProbeBackend` to the live device string.
- No-Vulkan-device fallback to Silk-CPU / Skia + the interactive pan/zoom gate on the Linux host.
- Zero-copy Vulkan→GL interop (external memory) is explicitly out of scope — V3 maps to CPU then
  uploads via the existing GL texture path, same as the D3D readback.

---

## 13. V4 findings (issue [#43](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/43)) — **RESOLVED: deep-zoom parity holds by construction; GPU perturbation re-filed as [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82)**

V4 was scoped as "validate the reference-orbit upload path deep on Vulkan and re-run the deep-zoom
gates." Investigation showed the scope rested on a **false premise inherited from §1**: that an HLSL
perturbation kernel exists and merely needed a Vulkan backend. It does not.

**What the code actually does (verified 2026-07-19, HEAD e3ebc23):**
- The only HLSL compute kernel is `Rendering.D3D/MandelbrotKernelSource.cs` (~290 lines): a plain
  FP32, split-hi/lo-centre, z²+c escape-time kernel. **No reference orbit, no δ-iteration, no
  rebasing** — nothing perturbation-related.
- `MandelbrotCalculator` dispatches a `GpuKernel` (D3D `MandelbrotGpuKernel` **or** Vulkan
  `VulkanComputeKernel`) **only** under `UseGpuCompute && GpuKernel != null && Zoom <= MaxGpuZoom`,
  and **`MaxGpuZoom = 1e4`**. Past 1e4 there is *no* GPU dispatch on any backend.
- Deep zoom is therefore **all-CPU**: SIMD perturbation (`ComputeRowPT`/`ComputePixelPTRebased`) over a
  DD/QD/OD reference orbit. `MandelbrotRefOrbitGpu` (ILGPU) can compute the *reference orbit* on a
  CUDA/OpenCL-FP64 device, but it is a single sequential orbit — the per-pixel δ loop never leaves the
  CPU. None of this is HLSL/SPIR-V; the Vulkan backend has no involvement.
- `--focusprobe` / `--navrepro` are headless **calculator** self-tests. In `Program.cs` they are
  dispatched *before* the `--renderer` parse and never construct a renderer — "run them on the Vulkan
  backend" is a no-op distinction.

**Conclusion — parity holds trivially.** Because `Zoom > MaxGpuZoom` short-circuits every GPU path,
`--renderer vulkan` and `--renderer dx` compute **bit-identical** deep-zoom frames (same CPU code).
There is nothing to port and nothing to gate beyond confirming the CPU path is healthy on HEAD:
`--focusprobe 96` → focus-err **0.00 px through 1e60**, then the expected flat dead-zone past
`maxUseful = 1e62` (a location property, see [Deep-Zoom-Perturbation.md](../Deep-Zoom-Perturbation.md)
§3 — not a bug, not a regression).

**The genuine feature — a GPU perturbation kernel — is net-new and re-filed as [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82).**
It would upload the reference orbit as an SSBO and run per-pixel δ + Zhuoran rebasing in-shader,
raising `MaxGpuZoom` far past 1e4. It does **not** exist on D3D either, so it is a cross-backend
capability, not a Vulkan port. Its headline risk is new: **QD/DD limb arithmetic in HLSL** (the CPU
reference orbit is up to OD/~124-digit; a float/double δ is fine but the on-GPU reference and the
`dc` term need enough precision). That risk is unproven under DXC→SPIR-V *and* FXC, so #82 leads with
a limb-math spike before any full build.

## 14. V6 spike findings (issue [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82)) — **SPIKE CLEARS: double perturbation loop ports to GPU; the feared QD/DD-limb-in-HLSL risk is avoidable**

Spike gate (`--vulkanpturbprobe`, `Rendering.Vulkan.Smoke/PerturbSpikeProbe.cs`) run 2026-07-19 on
**GT710** (Kepler, FP64 at 1/24 rate). It runs a self-contained `double` δ-rebased perturbation kernel
— `δ_{n+1} = (2·Z[m] + δ)·δ + dc` with Zhuoran rebasing (SM-2), the line-for-line twin of
`ComputePixelPTRebased` — as both a C# mirror and an HLSL kernel (DXC→SPIR-V), and compares.

**The headline #82 risk (QD/DD limb math in HLSL) turned out to be a non-issue at the loop level, for a
structural reason:** the production **default** deep-zoom path (`ComputePixelPTRebased`) already runs
the **δ-chain in plain `double`** — it reads the reference orbit's *Hi limb only* (`_refZr[m]` /
`_refZi[m]`, doubles) and a single-rounded `double` dc. The QD/OD precision is spent **building** the
reference orbit accurately (and carrying the OD *centre*), not in the per-pixel loop. So a GPU port
consumes a **double** reference-orbit SSBO and runs a **double** δ loop — no limb arithmetic in the
shader. The DD variant (`ComputePixelPTRebasedDD`, `UseDdRebaseReference`) is off by default and was
measured "insufficient" (§SM-11a); the spike confirms it is unnecessary at the tested depth.

**Results (centre = seahorse boundary point `-0.743643887037151 + 0.13182590420533i`, zoom 1e6,
96×96, maxIter 6000; ref orbit escapes at 3090, amplification ≈1e18.7 → non-degenerate frame,
distinct=1045, in-set 3904/9216):**

- **(1) Loop parity — PASS.** GPU vs CPU-`double`: **11/9216 (0.119%)** pixels disagree, all on
  filament pixels at the escape-time knife-edge (maxΔiter 68 there — expected boundary chaos, doc §2).
  The CPU-`double` vs a DD oracle (DD dc + DD δ, local TwoProduct/TwoSum, no FMA) disagrees by
  **9/9216 (0.098%)** — the *same order*. The GPU divergence sits **at the CPU precision noise floor**,
  i.e. no GPU/DXC dialect gap; it is the inherent chaotic sensitivity, not a port error. Gate is on the
  disagreement **fraction vs the noise floor** (same ULP-band philosophy as `--vulkanprobe`), never
  maxΔiter or a strict digest.
- **(2) dc precision — single `double` SUFFICES** at 1e6 (double-vs-DD divergence is at the δ noise
  floor). **Open for the full build:** re-run the same double-vs-DD comparison at 1e15/1e20 with a real
  OD centre — dc shrinks to ~1e-22 there and may need a split hi/lo `dc` (δ still stays double, doc §2).
- **(3) Both compilers accept the double math — PASS.** DXC `-spirv cs_6_0` emits the `Float64`
  capability and GT710 runs it; **FXC `cs_5_0` compiles the identical HLSL clean** (exit 0, 2636-byte
  CSO — verifies the D3D leg of the eventual shared `MandelbrotKernelSource` variant). No ICE on either
  (the ILGPU-FMA-ICE precedent did not bite — the loop uses only +,−,× on doubles).
- **(4) FP64 device gating — REQUIRED and wired.** `double` compute needs `shaderFloat64` enabled at
  device creation; `VulkanContext` did not request any feature. Fixed: it now queries + enables
  `shaderFloat64` when the device supports it and exposes `SupportsFloat64`; the probe prints **SKIP**
  (exit 0) on parts without it. Inert for the FP32 base/colour kernels (`--vulkanrenderprobe`
  unchanged: colour 0.085% / iter 0.171%). Perf is *not* gated here — GT710's 1/24-rate FP64 ran the
  correctness spike fine, but consumer-GPU throughput is a full-build measurement.

**Verdict — the full build is LOWER risk than #82 feared:** a plain-`double` kernel port
(reference-orbit SSBO + double δ + rebasing), spliced into `MandelbrotKernelSource` like the existing
base/colour variants, consumed by both FXC (D3D) and DXC (Vulkan). No in-shader limb math for the
default path. Remaining full-build work is engineering + the deep-`dc` precision re-check (2), not a
feasibility unknown. The full build stays a **separate, user-gated slice** (it touches production
render paths and raises `MaxGpuZoom`) — the spike's job was to de-risk it, and it has.

## 15. V6 full build (issue [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82)) — **LANDED (Vulkan): shared kernel + backend + calculator wiring, gated; D3D backend + GUI-enable are the fast-follow**

Built 2026-07-19 on the spike's findings, in two validated slices (commits `d4bae1c`, `db7274c`).

**Shared kernel + interface + Vulkan backend (`d4bae1c`).**
- `MandelbrotKernelSource.BuildPerturb()` — standalone `double` δ-rebased kernel (`CSPerturb`), the GPU
  twin of `ComputePixelPTRebased`: reference orbit as two `double` SSBOs (Hi-limb, the CPU `_refZr/_refZi`),
  Zhuoran rebasing, `dc = pixelOffset·scale`, outputs iter + smooth + finalZD(zr,zi,drv,div). No in-shader
  limb math (spike §14). Same two-compiler rule — **FXC `cs_5_0` and DXC `cs_6_0` both compile it**
  (verified; 3920-byte CSO under FXC).
- `IGpuKernel` gains `SupportsPerturbation` (default **false**) + `RunPerturb` (default **throws**) as C#
  default-interface members, so the D3D kernel compiles unchanged and opts in later; deep zoom stays CPU
  wherever `SupportsPerturbation` is false.
- `VulkanComputeKernel`: `SupportsPerturbation => ctx.SupportsFloat64`, and `RunPerturb` — dedicated
  perturb pipeline (b0 + refZr/refZi SSBOs + iter/smooth/finalZD), reference-orbit + 48-byte double param
  buffers. `BuildProgram` refactored to take an entry point + explicit binding numbers; base/colour paths
  unchanged (`--vulkanprobe` / `--colorprobe` / `--vulkanrenderprobe` still green).
- `--vulkanpturbprobe` now drives the **production** kernel object: GT710 GPU-vs-CPU **0.119%** at the CPU
  precision noise floor (double-vs-DD 0.098%), non-degenerate, exit 0.

**Calculator wiring (`db7274c`).**
- `MandelbrotCalculator.CalculateHighPrecision`: after the reference orbit is built, if
  `UseGpuPerturbation` **and** a perturbation-capable kernel is attached, dispatch the whole frame's
  δ-rebased loop to the GPU (`TryRunGpuPerturbation`) and do colour/dist/normal writeback on the CPU via
  the existing `FillAuxAndColorHP` — then skip the CPU SIMD/BLA/SA path. Runs the rebased loop for **every**
  pixel (glitch-free, SM-2). Offsets mirror `ComputeRowPTScalar` exactly. Any kernel failure falls through
  to the CPU path.
- Gated to the plain rebased regime it mirrors: recycle off, no per-tile cap, `UseDdRebaseReference` /
  `ForceScalarPtPath` off, `Zoom <= MaxGpuPerturbZoom`.
- **`MaxGpuPerturbZoom` = `ODZoomThreshold` (1e50)** — conservative ceiling; the δ loop is depth-independent
  (rebasing) but the deep-`dc` re-check (checkbox 2) gates lifting it. **`UseGpuPerturbation` — master
  toggle, DEFAULT OFF**, independent of `UseGpuCompute`.
- Gate `--vulkanpturbcalc` drives a real `MandelbrotCalculator` **both** ways at zoom 1e14: GPU-perturb
  frame vs CPU deep frame **disagree 0/16384 (0.000%)**. CPU deep path unregressed — `--focusprobe` green
  (0.00 px to 1e60, flat past `maxUseful=1e62`); the GPU path is gated off by default.

**Remaining (cross-backend fast-follow, each needs a validation surface this repo can't exercise headlessly):**
1. **D3D `MandelbrotGpuKernel.RunPerturb`** (Vortice: double cbuffer + reference-orbit SRVs + iter/smooth/
   finalZD UAVs; gate `SupportsPerturbation` on `D3D11_FEATURE_DATA_DOUBLES`). The shared HLSL already
   compiles under FXC; this is backend plumbing + a live-D3D-device parity test. Interface default keeps D3D
   opt-out (CPU deep zoom) until it lands.
2. **Enable in the GUI** — one line in `AvaloniaShellBootstrap` to set `UseGpuPerturbation=true` when the
   Vulkan backend is active and `SupportsFloat64`. Held back because it changes user-facing deep-zoom
   rendering — wants an interactive on-device sign-off first.
3. **Deep-`dc` precision (checkbox 2)** — re-run the double-vs-DD `dc` comparison at 1e15/1e20 with a real
   OD centre before raising `MaxGpuPerturbZoom` past 1e50.
4. Later: SA/BLA on GPU (non-goal for now), per-tile-cap support in the perturbation kernel.