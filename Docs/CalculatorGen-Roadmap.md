# CalculatorGen — Phase D+ Roadmap

Forward dev plan after the deep-zoom + hot-load + ops expansion landed.
Ordered top-down by recommended execution sequence: each phase
generally builds on capabilities from earlier phases. Items can be
re-ordered when one's blocked, but the dependency notes call out which
predecessors actually matter.

## Phase D-1 — Tooling polish (small, additive)

These don't change rendering output. Each is bounded and safe to land
between bigger features.

1. **CalcGen --analyze flag** — pretty-print parsed AST, simplified
   ∂p/∂z, ∂p/∂c, derived dz/dc update, SA recurrence (when applicable).
   No file output. Helps users debug equations before generating.
2. **Better parse error messages** — line/column from the lexer,
   "expected X, got Y" hints, suggestion when a keyword is misspelled
   (`cong` → did you mean `conj`?).
3. **Higher integer power cap** — Pow exponent currently capped at 16.
   Bump to 64. SA detector + emitters already iterate; no algorithmic
   change required.
4. **Per-equation custom bailout radius** — CLI flag `--bailout R`
   embeds the radius² as the template's `Bailout2` const. Phoenix /
   Nova / some user fractals need < 4; others want > 32.
5. **Compile cache for hot-load** — `CalculatorGenHotLoad` hashes the
   (equation, name) tuple, returns the previously-loaded `Type` if hash
   matches and the assembly is still loaded. Saves a Roslyn round-trip
   on duplicate compiles.
6. **Auto-unload previous assembly** — when a fresh compile succeeds,
   unload the prior `AssemblyLoadContext`. Prevents process bloat on
   rapid iteration.

## Phase D-2 — Quality + perf wins (medium)

7. **Per-pixel rebase** — when a pixel glitches the perturbation path,
   recompute a fresh reference orbit at that pixel's coordinate instead
   of falling all the way to HP-direct. Legacy uses this; biggest
   visible quality improvement at deep-zoom boundary regions where
   many pixels glitch around the same reference.
8. **Higher SA orders (3 → 16+)** — current SA tracks A, B, C only.
   Each extra order extends the valid skip range exponentially. Legacy
   uses ~64. Lifting to 16 captures most of the win.
9. **Cached SA tables** — extend the existing ref-orbit cache to
   include the SA coefficient arrays. Same cache key works (SA depends
   on the same inputs as ref orbit).
10. **Generic SA emitter (symbolic)** — current SA is hardcoded for
    z^d+c degrees 2..5. Derive the recurrence symbolically from any
    polynomial AST. Unlocks SA for sqr(z)+c with extra terms, for
    z²+az+c, etc. Builds on the AstPerturbation Taylor builder.
11. **DD-precision SA derivative** — track dz/dc through the SA skip
    using parallel coefficient arrays D₀, D₁, D₂, D₃. Restores distance
    estimate + surface normals in the SA-skipped region. Currently
    drv/div reset to zero at saStart.
12. **BLA hierarchy** — nested levels (single step, pairs, quads, …,
    log2(maxIt) levels). Each per-pixel iter tries the highest-level
    block that satisfies its validity radius. Legacy: 1.5–2× at deep
    zoom.
13. **Histogram equalization** — adaptive contrast pass over the
    iteration buffer. Legacy MandelbrotCalculator has it; generated
    calculators don't expose the underlying buffer the legacy
    histogram works against. Plumb the smooth-count buffer through.

## Phase D-3 — New equation capability (medium-large)

14. **Trig + transcendental ops** — `sin`, `cos`, `exp`, `log` AST
    nodes. Lexer + parser keywords. Differentiator rules. Emitters
    delegate to `Math.Sin` / `Math.Cos` / `Math.Exp` / `Math.Log`.
    Non-polynomial but holomorphic; distance estimate preserved.
15. **Conditional / piecewise equations** — `if Re(z) > 0 then a else b`
    grammar. Branch in emitted code. Distance estimate breaks at the
    discontinuity but smooth count works. Anti-holomorphic-equivalent
    flag for SupportsDe.
16. **Multi-variable poly in (z, c, z_{n-1})** — Phoenix-style two-step
    recurrence: z_{n+1} = z_n² + c + p · z_{n-1}. Add `prev` reference
    node, plumb second buffer through ref orbit + perturbation.

## Phase D-4 — Precision extensions (large)

17. **Octuple-double (OD) ref orbit** — 8-limb extended precision, ~124
    decimal digits. Push zoom ceiling past 1e50. Big build, but follows
    the QD pattern exactly — `OD` struct + `OdEmitter` + threshold
    property.
18. **DD-precision BLA tables** — at the deepest zooms, BLA `A_n` is
    near 1.0 + tiny; double precision in the table itself loses ULPs
    after thousands of accumulation steps. DD tables fix this. Pairs
    well with #12 (hierarchy).

## Phase D-5 — Visual quality (medium)

19. **Anti-aliasing** — 2×2 or 4×4 sub-pixel sampling option. Each
    pixel renders N internal samples, averages in colour space. Big
    perf cost — gate behind a Quality preset.
20. **Progressive rendering** — first render at ¼ resolution, present,
    refine to ½, then full. The renderer's overlay system can pop the
    refined frames in place.
21. **TAA / temporal accumulation** — when the camera is still, blend
    successive frames over time. Each frame's noise (sub-pixel jitter,
    palette dithering) averages out → smooth final image after a
    second of stillness.

## Phase D-6 — UX + tooling (medium)

22. **CalcGen --benchmark flag** — compile + time the generated
    calculator against a fixed set of viewpoints. Reports ms/frame at
    each zoom level. Useful for evaluating optimisation work in
    Phase D-2.
23. **Equation cookbook + gallery** — curated library of equation
    strings with metadata (default centre/zoom, thumbnail). UI dialog
    picks one, populates the UserEquation editor with the source.
24. **Live equation preview** — in UserEquation editor, show parsed AST
    + derived dz/dc + SA support flag as the user types. Surfaces
    parse errors immediately and confirms generator support.
25. **Animation: morph equations** — interpolate between two equations
    (z²+c → z³+c via t ∈ [0, 1]). Renders intermediate frames as a
    video. Requires SA support to be off during morph (recurrence
    changes per frame).
26. **Save hot-loaded calculator to permanent .cs** — button on the
    UserEquation dialog: takes the just-hot-loaded equation, writes
    the generated source to disk. Permanent variant of "Compile &
    Load".
27. **GPU reference orbit** — compute the ref orbit on the GPU
    alongside per-pixel work. Faster for large maxIt. Requires moving
    QD math onto the GPU; the existing ILGPU kernel only does plain
    double.
28. **Unit tests** — parser, differentiator, emitter golden-output
    tests. Catch regressions when adding new ops in Phase D-3.

## Phase D-7 — Architecture (large)

29. **Roslyn source generator** — replace the CLI + library API with a
    compile-time source generator. Mark a class with an attribute
    carrying the equation; the generator emits the implementation at
    project compile time. Drops the manual `dotnet run --project
    CalculatorGen` step and the `Calculators/Generated/` directory.

---

## Status snapshot

Tasks tracked in the session TaskList. Each phase item maps to one
task. Items 1–6 are estimated < 1 hour each; 7–18 are 1–4 hours; 19+
are larger refactors. Proceed in numbered order unless a dependency
flips priority.

### Completed

- [x] **Iteration index (`n` / `iter`) keyword** — user-equation
      bug: legacy C# `Step(Complex z, Complex c, int n)` signature uses
      `n` for iter count; pasting `z*z + c + 0.001*n` into Compile &
      Load hit CalcGen lexer with "Unknown identifier 'n'". Added
      `IterRef` AST node (real-valued scalar leaf), `iter` keyword
      with `n` as alias (both lex to TokenKind.Iter → IterRef).
      Differentiator: `IterRef → 0` (opaque, like PrevRef/DeltaRef).
      SaDetector rejects. CalculatorGenApi: `hasIter` flag gates
      `supportsPerturbation = false` (δ-Taylor can't linearise iter-
      dependent step — δ doesn't change n across ref orbit vs pixel);
      `supportsDe` stays true (iter is real scalar, no holomorphic
      chain participation). EmitterBase: new `IterRe` virtual binding
      plus `IterImLiteral` for the zero literal in each emitter's
      complex type ("0.0" / "Vector256<double>.Zero" / "(DD)0.0" /
      "(QD)0.0"); dispatcher routes IterRef → (IterRe, IterImLiteral)
      with ImZero=true so downstream Add/Mul elide dead-zero terms.
      Per-emitter binding: ScalarEmitter `iter`; Avx2Emitter `iter_v`
      (Vector256<double> broadcast); QdEmitter / QdDirectEmitter
      `iter_q` (QD); DdDirectEmitter `iter_dd` (DD). Template:
      `{{ITER_DECL_*}}` substitution pairs injected at every loop body
      site (8 sites: scalar fast × 3 incl GPU kernel, AVX2 × 2, QD
      ref orbit, scalar ref orbit, DD/QD direct + continue) — pulls
      from whatever loop counter is in scope (`it` or `n`) and casts
      to the per-emitter complex type. Empty when !hasIter so non-iter
      calcs generate byte-identical bodies. Tests: 90/90 PASS (5 new
      — both keyword forms, flag, diff, SA reject). All 6 stock
      calcs regenerate identically; `--gentest MandelbrotZ2` 0-diff.
      Sample equation `z*z + c + 0.001*n` produces clean bodies:
      scalar `double iter = (double)it; ... (0.001 * iter)`; AVX2
      `Vector256<double> iter_v = Vector256.Create((double)n); ...
      Avx.Multiply(z_re5, iter_v)`; QD `QD iter_q = (QD)(double)n;
      ... (0.001 * iter_q)`.
- [x] **Hot-load ILGPU ref + C# Complex preprocessor** — two UserEquation
      Compile-&-Load fixes landed together:
        1. `CalculatorGenHotLoad.GatherReferences()` walks AppDomain
           assemblies for Roslyn refs. ILGPU loads lazily (only on first
           GPU render attempt) — if user clicks Compile & Load before
           GPU runs, ILGPU is absent → generated `using ILGPU;` fails
           with CS0246. Fix: `TryLoadByName("ILGPU")` +
           `"ILGPU.Runtime"` + `"ILGPU.Algorithms"` before
           GatherReferences. Assembly probing path finds DLLs alongside
           host EXE.
        2. UserEquation textbox historically held C# `Complex.*`
           expressions (legacy `UserEquationCalculator` Roslyn-compiles
           them); CalcGen DSL is a restricted grammar (z, c, sin, cos,
           exp, log, +, -, *, /, ^Int, sqr, conj, fold, if/then/else,
           prev, abs). Same textbox, two grammars → CalcGen lexer
           rejected `Complex` as unknown identifier. New
           `EquationPreprocessor.Preprocess(string, out string?)` in
           CalculatorGen project translates:
             return X; → X      Complex.Zero → 0    Complex.One → 1
             Complex.Sin/Cos/Exp/Log/Conjugate(x) → sin/cos/exp/log/conj(x)
             Complex.Pow(x, k_int) → x^k (k>=2), x (k==1), 1 (k==0),
                                     1/x^|k| (k<0)
             Complex.Pow(x, expr)  → exp(expr*log(x))
           Hard-rejects `Complex.ImaginaryOne`, `new Complex(a, b)`,
           `Complex.Abs(x)` (DSL `abs` is squared-mag, not sqrt) with
           crisp error messages. Unknown `Complex.X` flagged with full
           supported list. Fixed-point loop handles nested calls
           (`Complex.Pow(Complex.Pow(z, 2), 3)`). Wired into both
           `OnHotLoadViaCalcGen` and `OnGenerateViaCalcGen` in
           `UserEquationViewModel`. Tests: 85/85 PASS (17 new
           preprocessor tests covering all translations + reject paths
           + the user's actual reported equation
           `return z * Complex.Pow(z,-3) + c * Complex.Pow(c,-2);`).
- [x] **1.** `--analyze` flag — `Program.cs` in CalculatorGen.
- [x] **2.** Better parse errors — line/col positions, "expected X, got Y"
      friendly token names in `Describe()`, Levenshtein keyword suggestion.
- [x] **3.** Higher integer power cap — 0..64 in `EquationParser.ParseFactor`.
- [x] **4.** `--bailout R` flag — embeds `R²` into template `Bailout2`.
- [x] **5.** Compile cache for hot-load — `_cache` dict in
      `CalculatorGenHotLoad`, keyed on `(equation, name)`.
- [x] **6.** Auto-unload previous assembly — `_lastContext.Unload()` on
      next successful compile; cache entries from dying context dropped.
- [x] **22.** `--benchmark` flag — host `Program.cs` arg; hot-compiles
      an equation and times the ladder default/shallow/mid-1e3/deep-1e6
      /deep-1e9. Writes `benchmark.out` next to the exe.
- [x] **28.** Unit tests — `CalculatorGenUnitTests.Run()` covers parser
      round-trip, lexer diagnostics (col + suggestions), differentiator
      ∂p/∂z + ∂p/∂c, simplifier identity rules, SA detector edges,
      anti-holomorphic feature flags. Invoke via `--calcgen-test`.
      Current: 36/36 PASS.
- [x] **9.** Cached SA tables — `_cachedSaSr/_cachedSaSi (jagged) +
      _cachedSaStart` in `Calculator.template.cs`, gated on
      `cacheHit && _cachedUseSa == UseSa && _cachedScale == scale`.
      Skips SA-build loop on zoom-only / theme-change frames.
- [x] **8.** Higher SA orders (3 → 8) — `SaRecurrenceEmitter` rewritten
      to take an `order` parameter, generates the full degree-d
      polynomial-mult unroll (dPow_m_k = m-fold convolution of S
      coefficients) for any (degree ∈ 2..5, order ≥ 2). Template
      switched from named A,B,C scalars to unrolled Sr1..Sr8/Si1..Si8
      with jagged `saSr[k][n]` storage. Tail-validity check is
      `|S_N · ε^N| < tol · |S_1 · ε|`. All 6 generated calculators
      regenerated; --calcgen-test 36/36 PASS, --gentest MandelbrotZ2
      PASS (scalar/AVX2/PT/BLA/QD-PT all agree). To bump to N=16,
      change `SaOrders = 8` const in template, expand the unrolled
      Sr/Si declarations + save/load blocks, and update the order
      default in `CalculatorGenApi.Generate`.
- [x] **11.** SA-skip derivative seed — drv/div seeded inside the
      `if (saStart > 0)` per-pixel block via the polynomial derivative
      `dz/dc = Σ_{k=1..N} k · S_k · ε^(k-1)`. Without the seed, drv/div
      restarted at 0 at saStart so distance estimate + surface normals
      collapsed across the SA-skipped band; the seed restores chain-
      rule continuity. Iteration count unchanged — self-test still
      0-diff. Note: roadmap originally called for parallel
      DD-precision D₀..D₃ arrays, but the closed-form polynomial
      derivative subsumes that: d(S_k)/d(c_pixel) = 0 because S_k
      depends only on the (constant) reference center, so the only
      surviving term is the chain through ε.
- [x] **8-fix.** SA overflow + δ-magnitude guards at deep zoom —
      three-layer stop criterion in the SA-build validity check:
      (1) `IsFinite + abs-cap > 1e150` short-circuits NaN/Inf
      propagation (S_N overflow → Inf−Inf = NaN slips past relative
      tolerance because `NaN > x` is false); (2) `aMag * maxEps > 0.25`
      bounds the SA-skipped |δ| inside the perturbation
      linearisation radius — without it, S_1 grows like |2Z|^n and
      the SA-skipped δ exceeds |Z|, breaking |δ| ≪ |Z| and pixels
      escape on iter saStart → solid-colour blob over frame centre;
      (3) original relative tail-vs-head tolerance preserved.
      Also lowered `ExtendedRefZoomThreshold` 1e12 → 1e9 to close
      the gap where plain-double ref orbit lost precision before
      QD engaged (legacy `MandelbrotCalculator` uses DD ref orbit
      always; adding the DD codegen path is a future task). Reported
      against `MandelbrotZ2` at center (-1.1727, -0.2968) zoom range
      1e12-1e16 where the SA-induced blob appeared.
- [x] **8-fix2.** SA per-order divergence check + tightened δ-bound —
      first fix's single tail-vs-head + δ<0.25 still let the blob
      reappear oscillating with correct frames as zoom advanced. Root
      cause from legacy `SeriesApproximation.FindSkip`: tail check
      misses "deep-k overskip" where a higher-order term grows faster
      than its predecessor, so the truncation tail dominates the kept
      terms even when the absolute tail bound passes. Added
      `|S_{k+1}|·maxEps ≤ tol·|S_k|` check for every consecutive pair
      (k=1..7), tightened δ-magnitude bound to BlaRelative (1e-3), and
      switched `SaTolerance` default 1e-6 → 1e-3 matching legacy's
      proven value.

- [x] **10.** Generic symbolic SA emitter — `AstSaDetector` extended
      with `DetectPolyInZPlusC(root)` that matches any polynomial in z
      (no CRef, Conj, Folded, Div anywhere) added to a single `+c`.
      Returns `(polyZ, degree)`. `SaRecurrenceEmitter.EmitGeneric` then
      derives the recurrence symbolically: for each k=1..degree it
      computes `(1/k!) ∂^k F / ∂z^k` via `AstDifferentiator`, simplifies,
      and renders the (Re, Im) expression at Z_n via `ScalarEmitter`
      into named locals `pk1_Re/Im..pkN_Re/Im`. Convolution + Σ logic
      shared with the pure z^d+c fast path. `CalculatorGenApi` prefers
      the fast path when applicable; falls back to generic for cases
      like `z²+az+c`, `2z³-z+c`, `z^6+c`, `z^4-z²+c`. Test coverage:
      `--calcgen-test` 42/42 PASS (6 new generic-detector tests).
- [x] **12.** BLA hierarchy — flat per-iter `blaAr/Ai/Br/Bi/R` arrays
      replaced with `FracturingFog.FFMath.BlaTable` (hierarchical,
      `log2(refLen)` levels, 2^k-step merged BLAs per level). Added
      `BlaTable(Bla[] level0, int refLen, double dcMaxAbs)` overload
      to `Math/Bla.cs` so generated calcs (any polynomial — A/B per
      equation via emitter) build level-0 from the per-iter
      `{{BLA_A_BODY}}` + `{{BLA_B_BODY}}` and the merge logic stays
      shared. Per-pixel iter calls `bla.Lookup(it, dMag2, maxIt)`;
      largest valid level applied via `δ ← A·δ + B·ε`, `drv ← A·drv`,
      `it += L − 1`. Cache: `_cachedBlaTable + _cachedBlaDcMaxAbs`
      gated on scale equality (BLA radii are scale-dependent, ref
      orbit isn't). Self-test PASS 36/36, `--gentest MandelbrotZ2` 0
      diff. Note: tested as potential fix for task #14 detail gap —
      ineffective; gap roots elsewhere.
- [x] **16.** Phoenix / multi-var two-step recurrence — new `PrevRef`
      AST node referencing z_{n-1}. Lexer keyword `prev` (Levenshtein
      suggestion list updated; `prv → prev` test). Parser atom case.
      Differentiator treats `prev` as opaque (∂prev/∂z = 0) — produces
      a WRONG dz/dc for Phoenix equations, but the value is never
      consumed because `hasPrev` gates `supportsDe = false` in
      `CalculatorGenApi` (proper Phoenix DE needs a parallel
      `dprev/dc` derivative-state vector updated as `dprev := dz` each
      iter — deferred). `supportsPerturbation` also gated off: Taylor
      δ-expansion needs a δ_prev companion to δ_z, also deferred.
      Printer prints `prev`. SaDetector + IsPureZPolynomial reject.
      EmitterBase: new abstract `PrevRe`/`PrevIm` bindings (default
      throw) + `Emit` dispatcher case routes PrevRef to them.
      Per-emitter bindings:
        - ScalarEmitter: `pr`/`pi`
        - Avx2Emitter: `pr`/`pi` (Vector256<double>)
        - QdEmitter: `pr_q`/`pi_q`
        - DdDirectEmitter: `pr_dd`/`pi_dd`
        - QdDirectEmitter: `pr_q`/`pi_q`
      Template carries state via new `{{PREV_DECL_*}}` / `{{PREV_UPDATE_*}}`
      substitution pairs — empty strings when `hasPrev=false` so non-
      Phoenix calcs generate byte-identical bodies vs pre-Item-16. Per
      iter, update sequence is `compute z_new from (zr, zi, pr, pi); pr
      := zr; pi := zi; zr := zr_new; zi := zi_new` — must save old
      `(zr, zi)` to `(pr, pi)` BEFORE the new-z commit. AVX2 prev
      update is masked by `activeMaskL` (BlendVariable) so escaped
      lanes keep their pre-escape prev. Wired into every loop that
      uses `{{SCALAR_Z_BODY}}` or `{{AVX2_Z_BODY}}` or `{{QD_Z_BODY}}`
      or `{{QD_DIRECT_BODY}}` / `{{DD_DIRECT_BODY}}` — scalar fast
      path (3 variants: IteratePixelScalar, IteratePixelScalarRaw, GPU
      kernel), AVX2 lane (2 variants), DD direct, QD direct, QD
      continue, QD ref orbit, scalar ref orbit. Generated Phoenix
      stock calc: `z*z + c + 0.5*prev` → `MandelbrotPhoenixCalculator`.
      Compiles, runs, no DE / no perturbation (HpDirect path active
      for any zoom > QdDirectZoomThreshold; otherwise scalar/AVX2
      double-only). Tests: 68/68 PASS (5 new — round-trip, flag, diff,
      SA, lexer suggestion). All 6 prior stock calcs regenerated;
      `--gentest MandelbrotZ2` 0-diff (substitutions empty on
      non-Phoenix calcs so byte-identical output).
- [x] **15.** Conditional / piecewise equations — `if cond then a else
      b` grammar. AST: new `If(Cond, Then, Else)` node (complex-valued)
      plus a separate `CondNode` / `CondTerm` mini-hierarchy used only
      inside conditions (`Cmp(op, l, r)`, `CondRe`, `CondIm`, `CondAbs2`,
      `CondConst`). Keeping cond terms in a separate type means the
      differentiator never has to differentiate non-holomorphic
      Re/Im/Abs2 nodes — they live exclusively on the boolean side.
      Lexer: `if`/`then`/`else`/`re`/`im`/`abs` keywords; `>`, `<`, `>=`,
      `<=`, `==`, `!=` operators with `=` and `!` alone rejected with
      hints to `==` / `!=`. Parser: `if` consumed at `ParseExpr` top so
      it binds loosest (whole then/else branches greedy); `re(...)` /
      `im(...)` extract scalars, `abs(...)` is sugar for |x|² (squared
      magnitude — saves the sqrt and matches typical bailout-style
      thresholds users think in). Differentiator: `d(If(c,t,e))/dz =
      If(c, dt/dz, de/dz)` — branches differentiate independently, cond
      stays untouched. Simplifier + printer + AstHelpers.Contains<T>
      recurse into branches and through CondTerms' embedded AstNodes.
      AstSaDetector + IsPureZPolynomial reject (piecewise is
      non-polynomial). CalculatorGenApi: new `hasCond` flag drives
      `supportsPerturbation = !(hasConj || hasFolded || hasDiv ||
      hasTrans || hasCond)`; `supportsDe` stays true (each branch is
      holomorphic on its own — the boundary locus is the only
      discontinuity and is measure-zero). Emitters: eager-evaluate
      both branches (matches SIMD lane semantics — every lane evaluates
      every branch), select on the rendered cond at the end.
        - **Scalar / Dd*Direct / Qd* / PerturbDeriv**: C# ternary on a
          rendered cond expression. DD/QD compare via `.Hi` / `.X0`
          high-limb access (sufficient for the threshold use cases —
          the low limbs only matter on a measure-zero locus).
        - **Avx2Emitter**: `Avx.Compare(left, right, FloatComparisonMode
          .Ordered{Gt,Lt,Ge,Le,Eq,Ne}NonSignaling)` → Vector256<double>
          mask → `Vector256.ConditionalSelect(mask, then, else)`.
          Ordered chosen so NaN operands compare false (matches C#).
        - **Avx512DerivEmitter**: same approach, 8 Vector512 lanes,
          `Avx512F.Compare` + `Vector512.ConditionalSelect`.
      Generation pipeline first attempt revealed PerturbDerivEmitter and
      QdEmitter also see `If` (PerturbDeriv via dz/dc chain even when
      perturbation is disabled, Qd because the QD reference orbit always
      runs the source equation); both gained `OpIf` accordingly. Tests:
      63/63 PASS (9 new — round-trip × 4, diff × 1, SA × 1, flag × 1,
      lexer × 2). All 6 stock calcs regenerated; `--gentest MandelbrotZ2`
      0-diff. Sample equation `if abs(z) > 4 then z else z*z + c`
      generates clean bodies in every path; `if re(z) > 0 then z*z + c
      else z*z*z + c` produces correct branched derivatives end-to-end.
- [x] **14.** Trig + transcendental ops — `Sin`, `Cos`, `Exp`, `Log`
      AST nodes. Lexer keywords + parser atoms. Differentiator chain
      rules: d/dz sin(u)=cos(u)·u', cos→-sin·u', exp→exp(u)·u',
      log→u'/u. Simplifier pass-through. Printer cases. AstHelpers
      Contains<T> recurses. AstSaDetector + IsPureZPolynomial reject
      (non-polynomial). CalculatorGenApi adds `hasTrans` flag; gates
      SupportsPerturbation off (Taylor δ-step not derived for
      non-poly nodes — perturbation/BLA/SA disabled when trig
      present). Emitters: ScalarEmitter uses complex identities
      sin(a+bi)=sin(a)cosh(b)+i·cos(a)sinh(b) (etc); Avx2Emitter +
      Avx512DerivEmitter emit per-lane scalar fallback (4 / 8 lanes
      via GetElement + Vector*.Create) since System.Math has no SIMD
      sin/cos; DdEmitter / DdDirectEmitter / QdEmitter /
      QdDirectEmitter promote .Hi / .X0 → double, call Math.X, demote
      back (precision degrades to ~16 digits inside the
      transcendental call; surrounding ±× preserved). DD/QD native
      transcendentals deferred to Phase D-4. PerturbDerivEmitter +
      Avx512DerivEmitter also need OpDiv (chain rule d(log(u))/dz
      injects u'/u even when source equation has no Div). Unit
      tests: 54/54 PASS (12 new — parser round-trip × 5, diff × 5,
      SA detector × 1, lexer suggestion × 1). All 6 stock calcs
      regenerated; `--gentest MandelbrotZ2` 0 diff.
- [x] **13.** Histogram equalization — `public int[] IterationBuffer`
      + `public float[] SmoothBuffer` exposed on every generated
      calculator. Allocated in `Resize()`. ColorFor + ColorForDd take an
      optional `bufIdx` param; when ≥ 0 they write iter + smooth to
      buffers alongside the colour map call. Every render path
      (SP scalar, AVX2 SP, GPU post-process, AVX-512 perturbation,
      scalar perturbation, HpDirect QD, HpDirect DD, PerPixelQdContinue)
      passes its pixel index through. `ApplyHistogramEqualization` +
      `BuildHistogramCdf` + `ApplyHistogramEqualizationWithCdf` ported
      from legacy `MandelbrotCalculator`, simplified — distance / normal
      / finalZ extras not plumbed (themes that need them degrade to
      smooth-count-only via `IColorMap` defaults). Tests: 36/36 PASS,
      `--gentest MandelbrotZ2` 0 diff.

### Known issues (separate tasks)

- **Pan/keyboard input fails at zoom ≥ ~1e24** — UI input layer
  updates `CenterX/CenterY` only, not the QD limbs. Pan delta at
  zoom 1e24 is ~1e-27, well below `CenterX`'s ULP (~2.6e-16 for
  |center| ~ 1). Update accumulates to zero. Fix lives in the
  Avalonia/WinForms pan-zoom command pipeline, not in CalcGen.

- **Generated perturbation loses detail past zoom 1e12** — probe
  (`--saprobe`) at user's report coords (-1.1727, -0.2968) shows
  generated PT path produces ~5× fewer unique colours per decade vs
  legacy `MandelbrotCalculator`. Workaround in `FractalRenderHost`:
  disable `UsePerturbation` for `MandelbrotZ2Calculator`, route
  through `TryRenderHpDirect` (QD per-pixel from iter 0) which
  probe confirms matches legacy exactly. Slower but correct. Root
  cause not isolated — tested Horner vs Taylor δ-step,
  DD-precision smooth count, glitch-bypass: all yield same numbers.
  Both gen and legacy run identical math on the same data type
  (QD ref orbit, double δ). Investigation needed: maybe AVX-512 lane
  vs legacy AVX-512 path subtly differ in escape capture, or the
  Avx512PerturbationEmitter's Taylor expansion order accumulates
  more rounding than legacy's hand-written `(2Z+δ)·δ` macro.
