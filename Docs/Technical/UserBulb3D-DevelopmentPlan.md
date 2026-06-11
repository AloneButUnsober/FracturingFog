# User Bulb 3D — Development Plan Prompt

> Companion pages: [Technical Index](_Index.md) · [User Bulb Sandbox Dev Plan](UserBulbSandbox-DevPlan.md) · [Fractal Equation Design Guide](FractalEquation-DesignGuide.md) · [User-facing User Bulb Guide](../User/UserBulb-Guide.md) · [Resources & Bibliography](../Resources-Bibliography.md)

> [!NOTE]
> **Snapshot 2026-06-11.** Stages 3A (Roslyn → ILGPU lowering) and 3B (quaternion axis GPU JIT) are
> live on `feature/gpu-compute` (commits `b79c2d0`, `e462e4e`). Stage 3C (sandbox interpreter perf)
> and chain-mode GPU dispatch are next. Re-check sequencing in
> [UserBulbSandbox-DevPlan.md](UserBulbSandbox-DevPlan.md) before resuming.

Drive agent execution of phased perf + creative work on the User Bulb 3D
calculator. Phases run in listed order. Each phase ends in a single commit.
Validate build + manual smoke render between phases.

## Locked decisions

1. **GPU backend:** ILGPU (CPU+GPU JIT, .NET-native, cross-vendor).
2. **Param bank:** arbitrary count. User adds/removes rows dynamically.
3. **Multi-equation:** arbitrary chain. Each step has a named output. Later
   steps reference earlier outputs by name.
4. **Quaternion:** unified with Vec3 path via axis-toggle (3D / 4D mode flag).
   No separate dialog.

## Target files (existing)

- `Calculators/UserBulbCalculator.cs` — main render loop.
- `Calculators/MandelbulbCalculator.cs` — reference for camera/light parity.
- `Models/Vec3.cs` — math helpers, expand surface area.
- `Models/FractalParameters.cs` — add new persisted fields + Clone() entries.
- `Models/UserBulbStore.cs` — preset persistence, extend schema.
- `Views/UserBulbDialog.cs` — UI editor, add new groups/tabs.
- `MainForm.cs` — viewport mouse-drag camera, render dispatch.
- `VideoZoom.cs` — animation export (re-hook time global in Phase 3.4).

## Global rules

- Commit per numbered task. Caveman commit messages via `/caveman-commit`.
- No emojis in code/UI.
- Preserve existing public API on `IFractalCalculator`.
- All hot-path math: `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- All new `FractalParameters` fields: add to `Clone()` in same commit.
- Default values must reproduce current visuals when feature is off/zero.
- Verify perf with [run](skills/run) skill after each Phase 1 + Phase 2 task.

---

## PHASE 1 — Quick Wins (perf)

Single commit at phase end. Target: 2-3× speedup, no UI changes.

### 1.1 Kill Interlocked counters
- File: `Calculators/UserBulbCalculator.cs:216-217, 225, 259, 283-284`
- Delete `hits`/`total` locals + 2× `Interlocked.Increment`.
- Wrap trailing `Debug.WriteLine` in `[Conditional("DEBUG")]` helper or delete.

### 1.2 Normalize3 → tuple, no alloc
- File: `Calculators/UserBulbCalculator.cs:342-347`
- Change sig: `private static (double X, double Y, double Z) Normalize3(double, double, double)`
- Add `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- Update callsites at `:193-214, :230, :265`. Use `.X/.Y/.Z` not `[0]/[1]/[2]`.

### 1.3 Vec3 inlining
- File: `Models/Vec3.cs:25-46`
- Add `[MethodImpl(MethodImplOptions.AggressiveInlining)]` to every operator,
  `Length`, `LengthSquared`, `Dot`, `Cross`, `Sin/Cos/Sinh/Cosh/Exp/Abs`,
  `Normalized`.

### 1.4 Bounding-sphere ray clip
- File: `Calculators/UserBulbCalculator.cs:233-251`
- Before raymarch loop: ray-vs-sphere(center=target, r=`UserBulbCullRadius`).
- Miss → write `InSetColor`, `continue`.
- Hit → advance `px,py,pz` to `tEnter`, set `tTotal=tEnter`.
- Add `FractalParameters.UserBulbCullRadius` (default 2.5) + Clone() entry.
- Expose in dialog "Render" group as `Cull r:` numeric.

### 1.5 Forward-diff normals
- File: `Calculators/UserBulbCalculator.cs:262-265`
- Replace central diff with forward diff: 3 probes instead of 6.
- Cache `dBase` from hit-step DE call; reuse as `f(p)` for `(f(p+h)-f(p))/h`.

### 1.6 Drop try/catch from inner loop
- File: `Calculators/UserBulbCalculator.cs:318-330`
- In `Compile()`: after successful Roslyn compile, smoke-test by invoking
  `fn(Vec3.Zero, new Vec3(0.5,0.5,0.5), 0)` and verifying all components
  `double.IsFinite`. On throw or non-finite → set `LastError`, null out
  `_compiled`.
- Remove try/catch inside `UserBulbDE`. Add `if (!double.IsFinite(r)) break;`
  after the `r = z.Length` line.

**Commit message:** `perf(userbulb): kill alloc/atomic/exception overhead in hot loop`

---

## PHASE 2 — Bigger Wins (perf)

Order: 2.1 → 2.2 → 2.3 → 2.4 → 2.6 → 2.5. Separate commit each.

### 2.1 Analytic-DE fast path
- New file: `Calculators/UserBulbAnalyticDE.cs`
- Source-pattern detector (regex on `UserBulbSource`):
  - `z*z + c` → square triplex DE: `dr = 2*r*dr + 1`
  - `Vec3.Pow(z, N) + c` → Hubbard-Douady: `dr = N*r^(N-1)*dr + 1`
  - Standard Mandelbulb triplex (sin/cos pattern) → power-N DE
- Return enum `AnalyticDEKind { None, Square, PowerN, Mandelbulb }` + params.
- In `UserBulbCalculator.Calculate`, branch on kind:
  - Non-None → single-trajectory DE (1× delegate call/iter)
  - None → existing 4-trajectory numerical Jacobian
- Add `FractalParameters.UserBulbDEMode` enum `{ Auto, Analytic, Numerical }`
  + Clone().
- UI: radio group in dialog "Render" section.
- Auto mode: at compile time, do 1 numerical probe at sample point, compare to
  analytic; if delta < 5%, mark eligible; else fall back to numerical.

**Commit:** `perf(userbulb): analytic DE fast path for detected power maps`

### 2.2 Progressive render
- File: `Calculators/UserBulbCalculator.cs`
- Add `bool LowResPreview { get; set; }` property.
- When true: render at W/2 × H/2, nearest-upscale to `ColorBuffer`.
- File: `MainForm.cs` — find existing drag-state flag (used by Mandelbulb).
  During drag set `LowResPreview=true`; on mouse-up set false + re-render.

**Commit:** `perf(userbulb): low-res preview during camera drag`

### 2.3 Cone-march prepass
- File: `Calculators/UserBulbCalculator.cs`
- New private: `ConeMarchTileMins(...)` — for each 16×16 tile, march center
  ray with `epsilon * tileRadius`. Output `double[]` tile `tMin` cache.
- Per-pixel raymarch initializes `tTotal = tileMins[tileIdx] * 0.95` (5%
  safety margin), `px,py,pz` advanced accordingly.
- Skip tiles where `tMin` exceeded raymarch limit (12.0).

**Commit:** `perf(userbulb): cone-march prepass for empty-space skip`

### 2.4 DynamicMethod IL emit
- File: `Calculators/UserBulbCalculator.cs:87-128`
- Replace `CSharpScript.Create/RunAsync` with:
  - `CSharpCompilation.Create` → emit assembly to memory `Stream`.
  - Load via `Assembly.Load(bytes)`, find `Step` method, `Delegate.CreateDelegate`.
- Preserve `LastError` surface (collect `Compilation.GetDiagnostics()`).
- Bench before/after with a `Stopwatch` per `fn(...)` call, log to debug.

**Commit:** `perf(userbulb): replace Roslyn script wrapper with direct emit`

### 2.6 Tile cache + reproject
- New file: `Calculators/UserBulbTemporalCache.cs`
- Store prior frame: `uint[] prevColor`, prior camera basis (fwd/right/up + pos).
- Each frame: per tile, reproject prior color via prior basis → screen NDC.
  Tiles within reprojection error budget reused; rest raymarched.
- Invalidate on:
  - Source recompile
  - `Quality` change
  - Any `FractalParameters` non-camera field change
  - Camera rotation delta > 5° per axis
- Add toggle `FractalParameters.UserBulbTemporalReuse` default true.

**Commit:** `perf(userbulb): temporal tile reuse across small camera deltas`

### 2.5 GPU compute backend (ILGPU)
- New file: `Calculators/UserBulbGpuCalculator.cs`
- NuGet: `ILGPU` package.
- New file: `Calculators/UserBulbIlgpuTranslator.cs` — walks Roslyn `SyntaxTree`
  for `UserBulbSource`, emits ILGPU-compatible C# (no closures, no heap alloc,
  fixed-arity Vec3 struct).
- Restrict supported user-source grammar (document allowed surface):
  - Allowed: arithmetic, `Math.*`, `Vec3.*`, `if/else`, ternary, local vars.
  - Disallowed: `new` for non-Vec3, loops, lambdas, attribute access on user-defined types.
- Kernel: `(Index2D, ArrayView<uint>, RenderParams)` → writes color per pixel.
- Add `FractalParameters.UserBulbBackend` enum `{ CPU, GPU }` + Clone().
- Dialog: combobox "Backend: CPU / GPU (experimental)".
- Fallback to CPU on translator failure; surface error in `LastError` label.
- Reuse `Accelerator` instance across renders; dispose on calculator dispose.

**Commit:** `feat(userbulb): ILGPU GPU compute backend for compatible sources`

---

## PHASE 3 — Creative Additions

### 3.1 Vec3 helper lib expansion
- File: `Models/Vec3.cs`
- Add statics (all inlined):
  - `Pow(Vec3 v, double n)` — triplex spherical power.
    - `r = v.Length; theta = atan2(v.Y, v.X); phi = asin(v.Z / r);`
    - Return `r^n * (cos(n*phi)*cos(n*theta), cos(n*phi)*sin(n*theta), sin(n*phi))`.
    - Guard `r < 1e-12` → return Zero.
  - `Rot(Vec3 v, Vec3 axis, double angle)` — Rodrigues formula.
  - `BoxFold(Vec3 v, double limit)` — per-axis `abs(x)>limit ? sign(x)*2*limit - x : x`.
  - `SphereFold(Vec3 v, double rMin, double rMax)` —
    - `r2 = v.LengthSquared`
    - `r2 < rMin² → v * (rMax²/rMin²)`
    - `rMin² ≤ r2 < rMax² → v * (rMax²/r2)`
    - Else `v`.
  - `AbsX/AbsY/AbsZ` — selective per-axis abs.
  - `Mod(Vec3 v, double period)` — `v - period*floor(v/period + 0.5)`.
  - `SMin(double a, double b, double k)` — `-log(exp(-k*a) + exp(-k*b)) / k`.
  - `ToSpherical(Vec3 v)` → `(r, theta, phi)` tuple.
  - `FromSpherical(double r, double theta, double phi)` → Vec3.

**Commit:** `feat(vec3): expand math lib for bulb formula authoring`

### 3.2 Quaternion type (unified mode toggle)
- New file: `Models/Quat.cs`
  - `readonly record struct Quat(double W, double X, double Y, double Z)`
  - Ops: `+`, `-`, `*` (Hamilton), scalar `*`, `Length`, `LengthSquared`,
    `Conjugate`, `Dot`.
  - Static: `FromVec3(Vec3 v, double w=0)`, `ToVec3() => new Vec3(X,Y,Z)`.
- File: `Calculators/UserBulbCalculator.cs`
  - Add `FractalParameters.UserBulbAxisMode` enum `{ Vec3, Quat }` + Clone().
  - Add second compile path: when Quat mode, wrap as
    `Quat Step(Quat z, Quat c, int n)`.
  - DE in Quat mode: analytic `dq' = 2*q*dq` for square map; numerical
    Jacobian on the W/X/Y/Z perturbations for arbitrary.
  - Color/normal: project Quat → Vec3 via `.ToVec3()` for raymarch position.
  - User code in Quat mode: `z.W` accessible; `c` 4D too (4th coord = slice
    plane position).
- File: `Models/FractalParameters.cs`
  - `public double UserBulbQuatSliceW { get; set; } = 0.0;` — 4D slice plane.
- File: `Views/UserBulbDialog.cs`
  - Axis-mode combobox "Algebra: Vec3 / Quat".
  - On Quat: enable `Slice W:` numeric. Hint label updates to show `Quat`
    signature.

**Commit:** `feat(userbulb): unified Vec3/Quat algebra mode with W-slice`

### 3.3 Parameter slider bank — arbitrary count
- File: `Models/FractalParameters.cs`
  - `public List<UserBulbParam> UserBulbParams { get; set; } = new();`
  - Clone(): deep-copy list.
- New file: `Models/UserBulbParam.cs`
  - `record class UserBulbParam(string Name, double Value, double Min, double Max);`
- File: `Calculators/UserBulbCalculator.cs:130-140` `WrapUserSource`
  - Compile sig: `Func<Vec3, Vec3, int, double[], Vec3>` (and Quat variant).
  - Inject before body: `double {p.Name} = _p[i];` for each param (validate
    name is valid C# identifier; reject duplicates at save time).
  - Call site: pass `UserBulbParams.Select(p => p.Value).ToArray()` per render
    (cache, only rebuild on param-list mutation).
- File: `Views/UserBulbDialog.cs`
  - New collapsible "Params" group below "Render".
  - `+` button → add row: [name TextBox] [value NumericUpDown] [min] [max]
    [delete X].
  - Value change → `RenderRequested` (no recompile).
  - Name/min/max change → recompile (name affects wrap source).
  - Persist with saved equation in `UserBulbStore`.
- File: `Models/UserBulbStore.cs`
  - Extend entry schema: `List<UserBulbParam> Params`.

**Commit:** `feat(userbulb): arbitrary-count named parameter sliders`

### 3.4 Time global `t` for animation
- File: `Models/FractalParameters.cs`
  - `public double UserBulbTime { get; set; }` (not cloned for time-line; or
    do clone — decide at impl time, default: clone).
- File: `Calculators/UserBulbCalculator.cs`
  - Compile sig adds `double t` param.
  - Wrap source: prepend `double t = _t;`.
- New file: `Views/UserBulbAnimateBar.cs`
  - Play/Pause toggle.
  - Speed slider (units per second, -5..5).
  - Loop length (seconds, 0 = no loop).
  - `System.Windows.Forms.Timer` 30Hz: `_params.UserBulbTime += speed * dt;
    RenderRequested?.Invoke();`
- File: `VideoZoom.cs`
  - Add "Time sweep" mode: sweep `UserBulbTime` from A→B over N frames,
    capture each.

**Commit:** `feat(userbulb): animation time global + animate bar + video sweep`

### 3.5 Julia mode (fixed c)
- File: `Models/FractalParameters.cs`
  - `public bool UserBulbJuliaMode { get; set; }`
  - `public Vec3 UserBulbJuliaC { get; set; } = new(-0.2, 0.4, 0.0);`
  - Clone() entries.
- File: `Calculators/UserBulbCalculator.cs:303-340` `UserBulbDE`
  - If Julia: `cBase = JuliaC`. Jacobian now w.r.t. z (initial), not c.
    Perturb initial z trajectory: `z0Px = (h,0,0)`, etc.
  - Effectively swaps which input is held fixed.
  - Add `bool juliaMode` param to `UserBulbDE`; route from `Calculate`.
- File: `Views/UserBulbDialog.cs`
  - Checkbox "Julia mode" + 3 NumericUpDown rows for `JuliaC.X/Y/Z`.
  - Both fire `RenderRequested` only.
- Quat-mode (3.2) interaction: `UserBulbJuliaC` becomes Quat type; UI shows
  4 numerics when in Quat mode.

**Commit:** `feat(userbulb): Julia mode with fixed-c (Vec3/Quat)`

### 3.7 Color drivers
- File: `Models/FractalParameters.cs`
  - `public enum BulbColorDriver { StepDepth, OrbitTrap, EscapeAngle, FinalMagnitude, IterComponent, Normal }`
  - `public BulbColorDriver UserBulbColorDriver { get; set; }`
  - `public Vec3 UserBulbOrbitTrap { get; set; } = Vec3.Zero;`
  - `public int UserBulbIterComponent { get; set; } = 0;` (0=X, 1=Y, 2=Z)
  - Clone().
- File: `Calculators/UserBulbCalculator.cs`
  - `UserBulbDE` tracks: `minTrapDist` (min distance to `UserBulbOrbitTrap`
    across iters), final `z`, final `r`, escape iter.
  - Return struct `DEResult { double Dist; double TrapMin; double FinalR;
    Vec3 FinalZ; double EscapeIter; }`.
  - Color computation `:274-276` switches on driver.
- File: `Views/UserBulbDialog.cs`
  - Combobox "Color driver".
  - Conditionally show: trap XYZ (OrbitTrap), axis selector (IterComponent).

**Commit:** `feat(userbulb): color drivers (orbit trap, angle, magnitude, axis, normal)`

### 3.8 Lighting (3-light + shadows + AO + fog + sky)
- File: `Models/FractalParameters.cs` (port from existing theme system; see
  commit `d420ff4` for 3rd rim light reference)
  - `UserBulbLight2Theta/Phi/ColorR/G/B/Intensity` (default off, intensity=0)
  - `UserBulbLight3Theta/Phi/ColorR/G/B/Intensity` (default off)
  - Re-use `UserBulbLightTheta/Phi` as Light1; add `Light1Color*` + `Intensity`.
  - `UserBulbShadowSoft` 0..1 (0=off, 1=hard, else penumbra width).
  - `UserBulbAOSamples` 0..8 (0=off).
  - `UserBulbAOStrength` 0..1.
  - `UserBulbFogDensity` 0..2 (0=off).
  - `UserBulbBgTopColor` / `UserBulbBgBottomColor` (uint ARGB).
- File: `Calculators/UserBulbCalculator.cs`
  - Loop over enabled lights, sum diffuse * color * intensity.
  - Soft shadow: from hit point, march toward each light. Track
    `minRatio = min(DE/tToLight)`. Penumbra = `smoothstep(0, soft, minRatio)`.
  - AO: 5 taps along surface normal at `r, 2r, 4r, 8r, 16r` distances.
    `occl = 1 - strength * sum((maxR - hit_d)/maxR)`. Clamp [0,1].
  - Fog: `fogF = 1 - exp(-tTotal * density)`. Mix shaded color toward bg.
  - BG ray (miss): vertical gradient `lerp(BgBottom, BgTop, 0.5*(rdy+1))`.
- File: `Views/UserBulbDialog.cs`
  - New tab/group "Lighting": 3 light rows + shadow/AO/fog/bg color pickers.

**Commit:** `feat(userbulb): 3-light shading + soft shadows + AO + fog + sky gradient`

### 3.9 Camera & view
- File: `Models/FractalParameters.cs`
  - `UserBulbFovDegrees` default 60.
  - `UserBulbDoFAperture` default 0 (off).
  - `UserBulbDoFFocusDist` default 0.
  - `UserBulbDoFSamples` default 8.
  - `UserBulbClipPlaneNormal` (Vec3) + `UserBulbClipPlaneDist` (0 = off).
  - `UserBulbSuperSample` enum `{ x1, x2, x4 }`.
- File: `Calculators/UserBulbCalculator.cs:206`
  - Replace `Math.PI / 3.0` with `Fov * Math.PI / 180`.
  - DoF: when aperture>0, per pixel jitter ray origin on disc tangent to
    camera basis; accumulate `Samples` rays; average.
  - Clip: in `UserBulbDE`, if `Vec3.Dot(p, clipN) > clipD` → return huge dist
    (skip surface).
  - SS: render at `W*ss × H*ss` into temp buffer, box-filter downsample to
    `ColorBuffer`.
- File: `Views/UserBulbDialog.cs`
  - New "View" group: FOV slider, DoF aperture/focus/samples, Clip plane
    enable+normal+dist, SS combobox.
- File: `MainForm.cs`
  - Find existing Mandelbulb viewport mouse handler; fork for UserBulb fractal
    type:
    - Left-drag: `CameraTheta += dx * sensitivity`, `CameraPhi -= dy *
      sensitivity` (clamp `Phi` to `[0.01, π-0.01]`).
    - Wheel: `CameraDistance *= (1 - delta * 0.1)`.

**Commit:** `feat(userbulb): FOV/DoF/clip/supersample + viewport mouse orbit`

### 3.6 Multi-equation arbitrary chain w/ named outputs
- File: `Models/FractalParameters.cs`
  - `public List<UserBulbChainStep> UserBulbChain { get; set; } = new();`
  - Clone().
- New file: `Models/UserBulbChainStep.cs`
  - `record class UserBulbChainStep(string OutputName, string Source);`
  - `Source` is body of `Vec3 Step(Vec3 z, Vec3 c, int n, ChainCtx ctx) => ...;`
    where `ctx.{name}` accesses prior step outputs.
- File: `Calculators/UserBulbCalculator.cs`
  - Compile each step; cache `Func<Vec3,Vec3,int,ChainCtx,Vec3>` list.
  - Per iter: execute each step in order, store outputs by name in `ChainCtx`.
  - Final iter z = last step's output (or designated "final" step).
  - Numerical Jacobian: replay full chain w/ perturbed c (4× chain cost).
- New file: `Models/ChainCtx.cs`
  - `class ChainCtx { Dictionary<string, Vec3> Outputs; public Vec3 this[string k] => Outputs[k]; }`
  - Roslyn imports must include namespace.
- File: `Views/UserBulbDialog.cs`
  - Replace single editor with a chain list:
    - Toolbar: `+ Add step`, `− Remove`, `↑/↓ Reorder`.
    - Each step row: [name TextBox] [collapsed/expanded source TextBox].
  - Backward compat: if `UserBulbSource` set + chain empty, on load auto-
    convert to a single chain step named "out".

**Commit:** `feat(userbulb): arbitrary-length step chain with named outputs`

### 3.10 Preset library
- File: `Models/UserBulbStore.cs`
  - On first `Load()` where store is empty, seed with presets. Each preset
    populates: chain source, params, camera, lighting, color driver, axis mode.
  - Presets:
    - Mandelbulb p=8 (`Vec3.Pow(z, 8) + c`)
    - Mandelbulb squared (`z*z + c` triplex)
    - Sin-bulb (`Vec3.Sin(z) * Vec3.Cosh(z) + c`)
    - Abs-bulb (`Vec3.Pow(Vec3.Abs(z), 8) + c`)
    - Mandelbox (`Vec3.BoxFold(z, 1) * scale + c` with sphere fold)
    - Kaleidoscopic IFS (chain: fold → rot → scale, 3 steps)
    - Quaternion Julia (Quat mode, `z*z + c`, fixed c)
    - Menger sponge step (`abs` + fold combo)
    - Sierpinski tetrahedron (3 reflections per iter)
    - Animated breathing bulb (uses `t` global)

**Commit:** `feat(userbulb): seed preset library on first run`

### 3.11 Export
- New file: `Export/UserBulbMeshExporter.cs`
  - Sample DE on N³ grid (default 128) within bounding cube.
  - Marching cubes → triangle list.
  - Writers: OBJ, STL (binary).
- File: `Views/UserBulbDialog.cs`
  - "Export mesh…" button → save dialog (.obj/.stl) + grid-resolution prompt.
- File: `VideoZoom.cs`
  - Already covered by 3.4 time sweep. Confirm path works end-to-end.

**Commit:** `feat(userbulb): marching-cubes mesh export (OBJ/STL)`

### 3.12 .fbulb import/export
- File: `Models/UserBulbStore.cs`
  - Existing JSON schema → extend to capture full entry (chain, params,
    camera, lights, color, axis mode, presets).
  - `Export(string name, string filePath)` — write one entry as .fbulb JSON.
  - `Import(string filePath)` — read, merge or rename on name collision.
- File: `Views/UserBulbDialog.cs`
  - Buttons: "Import…" / "Export…" beside `Save/Delete`.

**Commit:** `feat(userbulb): .fbulb single-equation import/export`

---

## Per-phase verification

After each commit:

```powershell
dotnet build
```

Then launch and smoke-render:

```powershell
dotnet run --project FracturingFog.csproj
```

(Or invoke [run](skills/run) skill if available.)

Validate:

- Build succeeds, no warnings introduced.
- App launches.
- Switch fractal type to "User Bulb (3D)".
- Default source renders without exception.
- Quick visual A/B vs prior commit (HEAD~1 build) — no regression.

## Cross-cutting concerns

- **Preset migration:** when adding new `FractalParameters` fields, ensure
  `UserBulbStore` JSON deserializer tolerates missing fields (use defaults).
- **Backward compat:** `UserBulbSource` field stays for old presets; auto-
  convert to chain step on load (Phase 3.6).
- **GPU + Quat:** ILGPU backend (Phase 2.5) initially Vec3-only. Document
  Quat as CPU-only until follow-up.
- **GPU + chain (3.6):** translator must handle multi-step chain. Defer until
  3.6 lands; until then GPU runs single-source compatible chains only.
- **GPU + params (3.3):** pass param array as ILGPU `ArrayView<double>`.
- **Caveman commits:** every commit message via `/caveman-commit`.

## Open follow-ups (post-plan)

- VR / stereo rendering.
- Reflections / refractions (1 bounce).
- Sub-surface scattering.
- Real-time GPU denoiser.
- Web (WASM) port of CPU path for embedding.
