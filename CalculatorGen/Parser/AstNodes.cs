// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AstNodes.cs
//
// AST node types for the polynomial-in-(z,c) grammar accepted by Phase A
// of CalculatorGen, extended in Phase B with a DRef node (the symbolic
// dz/dc derivative state) and in Phase C with DeltaRef + EpsRef nodes
// (per-pixel δ and ε offsets used by the perturbation expansion).
//
// Restricted grammar (z, c, integer powers, +, -, *, parens, unary -)
// keeps the differentiator and emitters trivial and lets the same AST
// later drive perturbation/BLA/SA expansion.

namespace FracturingFog.CalculatorGen.Parser;

public abstract record AstNode;

/// <summary>Complex variable 'z' — the iterating value. In perturbation
/// emission it represents the reference-orbit iterate Z_n; in everything
/// else it represents the per-pixel iterate.</summary>
public sealed record ZRef : AstNode;

/// <summary>Complex variable 'c' — the parameter (pixel coordinate, or
/// view-centre C during perturbation emission).</summary>
public sealed record CRef : AstNode;

/// <summary>Symbolic placeholder for the current dz/dc value. Appears only
/// in derivative ASTs constructed by <see cref="AstDifferentiator"/>; the
/// emitter binds it to runtime registers (dr, di).</summary>
public sealed record DRef : AstNode;

/// <summary>Per-pixel orbit offset δ = z − Z (Tier 4 perturbation). Bound
/// by the perturbation emitter to runtime δ registers.</summary>
public sealed record DeltaRef : AstNode;

/// <summary>Per-pixel parameter offset ε = c − C (Tier 4 perturbation).
/// Bound by the perturbation emitter to runtime ε registers.</summary>
public sealed record EpsRef : AstNode;

/// <summary>Iteration index n (current loop counter). Real-valued scalar,
/// imaginary part is always zero. Lets user equations encode iter-
/// dependent dynamics (e.g. a slow drift `c + 0.001*n`). Treated as
/// opaque by the differentiator (∂n/∂z = 0) — that's correct since n
/// is a loop counter, not a complex variable. Distance estimate is
/// unaffected (iter is a real scalar in the holomorphic chain).
/// Perturbation Taylor expansion IS broken though — δ doesn't change n,
/// so iter-dependent step functions can't be linearised around a
/// reference orbit. Gated off via SupportsPerturbation=false when
/// present.</summary>
public sealed record IterRef : AstNode;

/// <summary>Previous-iterate placeholder: z_{n-1}. Enables Phoenix-style
/// two-step recurrences (e.g. z_{n+1} = z_n² + c + p·z_{n-1}). The
/// emitter binds it to per-iteration state held alongside z; the
/// surrounding template threads (pr, pi) initialised to zero and
/// updated as `prev := z` before each new z computation. Treated as
/// opaque by the differentiator (∂prev/∂z = 0) — distance estimate is
/// disabled when present because tracking dprev/dc requires a second
/// derivative state vector (Phoenix-aware DE is a future extension).
/// Perturbation also disabled (would need δ_prev companion to δ_z).
/// </summary>
public sealed record PrevRef : AstNode;

/// <summary>Real-valued numeric literal. Treated as complex (n, 0).</summary>
public sealed record RealConst(double Value) : AstNode;

/// <summary>Imaginary unit literal. Treated as complex (0, 1). Holomorphic
/// constant — Wirtinger ∂i/∂z = 0, but the chain rule still works correctly
/// through Mul (e.g. d(i·z)/dz = i). Distance estimate stays valid. SA
/// detector accepts it as a degree-0 complex constant — `i·z² + c` is still
/// degree-2 z polynomial with a complex coefficient. Perturbation Taylor
/// expansion handles it transparently via the symbolic differentiator
/// (returns 0, treated like any other constant under δ/ε expansion).</summary>
public sealed record ImagUnit : AstNode;

/// <summary>Unary negation.</summary>
public sealed record Neg(AstNode Operand) : AstNode;

/// <summary>Complex addition.</summary>
public sealed record Add(AstNode Left, AstNode Right) : AstNode;

/// <summary>Complex subtraction.</summary>
public sealed record Sub(AstNode Left, AstNode Right) : AstNode;

/// <summary>Complex multiplication.</summary>
public sealed record Mul(AstNode Left, AstNode Right) : AstNode;

/// <summary>Integer power. Base must reduce to z or c (no Pow on general
/// subexpressions). Exponent must be a small non-negative integer.</summary>
public sealed record Pow(AstNode Base, int Exponent) : AstNode;

/// <summary>General power <c>pow(base, exp)</c> with an arbitrary complex
/// exponent (negative, fractional, or complex). Matches the SandboxExpression
/// runtime's <c>pow</c>: when BOTH operands are provably real the emitter uses
/// <c>Math.Pow</c> (real result, so <c>pow(-2, 3) = -8</c> exactly); otherwise
/// <c>System.Numerics.Complex.Pow</c> (principal branch, zero-guarded so
/// <c>pow(0,0)=1</c> and <c>pow(0,k)=0</c>). #27 Phase 6 (tranche 2) — this
/// unblocks negative/fractional-power maps in compiled kernels. Distinct from
/// the integer-only <see cref="Pow"/> node (the <c>^</c> operator). Transcendental
/// + non-holomorphic under δ-expansion: perturbation / SA / analytic DE all gate
/// off (folded into hasTrans / hasArg), so it renders on the direct scalar /
/// AVX2 / DD / QD paths at shallow zoom where parity holds.</summary>
public sealed record PowC(AstNode Base, AstNode Exp) : AstNode;

/// <summary>Complex division. Anti-pole behaviour at |b| == 0 is left to
/// the emitted code; for typical Mandelbrot-style equations the iterate
/// rarely lands exactly on a pole.</summary>
public sealed record Div(AstNode Left, AstNode Right) : AstNode;

/// <summary>Complex conjugate. (re, im) → (re, -im). Anti-holomorphic:
/// ∂conj(z)/∂z = 0 in the Wirtinger sense. Equations containing this
/// node disable distance-estimate output — the dz/dc chain rule no
/// longer carries a meaningful value.</summary>
public sealed record Conj(AstNode Operand) : AstNode;

/// <summary>BurningShip-style component fold: (re, im) → (|re|, |im|).
/// Like Conj, non-holomorphic — disables distance estimate.</summary>
public sealed record Folded(AstNode Operand) : AstNode;

// Per-component real functions applied independently to Re and Im (reducing
// to (F(re), 0) when the operand is provably real). #27 Phase 6 (tranche 2) —
// parity with the SandboxExpression runtime, which lifts these the same way.
// Non-holomorphic (piecewise-constant / kinked): perturbation / SA / analytic
// DE all gate off, so they render on the direct scalar / AVX2 / DD / QD paths.

/// <summary>Per-component floor: (⌊re⌋, ⌊im⌋).</summary>
public sealed record Floor(AstNode Operand) : AstNode;

/// <summary>Per-component round-half-to-even (Math.Round): (round(re), round(im)).</summary>
public sealed record Round(AstNode Operand) : AstNode;

/// <summary>Per-component ceiling: (⌈re⌉, ⌈im⌉).</summary>
public sealed record Ceil(AstNode Operand) : AstNode;

/// <summary>Per-component truncate-toward-zero: (trunc(re), trunc(im)).</summary>
public sealed record Trunc(AstNode Operand) : AstNode;

/// <summary>Per-component fractional part x−⌊x⌋: (re−⌊re⌋, im−⌊im⌋). Useful for
/// domain-warping / tiling / Kali-style maps.</summary>
public sealed record Fract(AstNode Operand) : AstNode;

/// <summary>Per-component sign (Math.Sign): (sign(re), sign(im)) ∈ {−1,0,1}².</summary>
public sealed record Sign(AstNode Operand) : AstNode;

/// <summary>Complex sine. sin(a+bi) = sin(a)cosh(b) + i cos(a)sinh(b).
/// Holomorphic — distance estimate via d/dz sin(u) = cos(u)·u' preserved.
/// SA / BLA detector rejects this node (non-polynomial); perturbation
/// Taylor expansion is not derived — when present, perturbation is
/// disabled and the renderer falls back to HpDirect for deep zoom.</summary>
public sealed record Sin(AstNode Operand) : AstNode;

/// <summary>Complex cosine. cos(a+bi) = cos(a)cosh(b) − i sin(a)sinh(b).
/// Holomorphic. See <see cref="Sin"/> for capability notes.</summary>
public sealed record Cos(AstNode Operand) : AstNode;

/// <summary>Complex exponential. exp(a+bi) = e^a·(cos(b) + i sin(b)).
/// Holomorphic. See <see cref="Sin"/> for capability notes.</summary>
public sealed record Exp(AstNode Operand) : AstNode;

/// <summary>Complex natural logarithm.
/// log(a+bi) = (1/2)·log(a²+b²) + i·atan2(b, a).
/// Holomorphic on C\{0}. See <see cref="Sin"/> for capability notes.</summary>
public sealed record Log(AstNode Operand) : AstNode;

/// <summary>Complex principal square root (System.Numerics.Complex.Sqrt).
/// INTERNAL node — there is no <c>sqrt</c> surface syntax; the lexer/parser
/// never produce it. It is emitted ONLY by <see cref="AstDifferentiator"/> as
/// part of the analytic-DE derivative rules for the inverse trig / hyperbolic
/// functions (∂asin(u)/∂v = u'/√(1−u²), etc.). Because it appears only inside
/// an already-differentiated tree (never re-differentiated in the first-order
/// dz/dc chain), only the emitters that walk a derivative AST need to lower it
/// (Scalar / Avx2 direct-DE + the three perturbation-deriv emitters); the
/// z-update direct emitters (DD / QD) never see it. Always lowered via the
/// full complex Sqrt (NOT Math.Sqrt) so a negative real radicand — e.g.
/// √(1−u²) for a real |u|>1 — yields the correct imaginary result.</summary>
public sealed record Sqrt(AstNode Operand) : AstNode;

// Inverse trigonometric / hyperbolic functions. #27 Phase 6 (tranche 2).
// Holomorphic on their principal branches, BUT the emitters compute them via
// System.Numerics.Complex.{Asin,Acos,Atan} and the log-formula continuations
// for asinh/acosh/atanh — identical to the SandboxExpression runtime. No
// closed-form δ-Taylor / SA recurrence is derived, so all six keep perturbation
// / SA gated off (folded into hasTrans); they render on the direct scalar /
// AVX2 / DD / QD paths at shallow zoom where parity holds.
//
// ANALYTIC DE (#215): the first-order dz/dc chain rules ARE implemented
// (∂asin(u)/∂v = u'/√(1−u²), ∂atan(u)/∂v = u'/(1+u²), etc. — see
// AstDifferentiator), so SupportsDe stays ON and surface normals / exterior
// distance estimate work for these maps on the shallow direct path. The
// √ radicand is lowered through the internal <see cref="Sqrt"/> node.
//
// SEMANTIC NOTE: SandboxExpression returns a REAL result (Math.Asin etc.) for
// a provably-real operand inside the principal real domain, and the complex
// continuation outside it. CalcGen emits the uniform complex form for every
// operand — numerically ≈ the real branch inside the domain (a negligible,
// escape-mask-invisible difference), and bit-identical to Sandbox's complex
// branch (same BCL call / same formula) everywhere else.

/// <summary>Complex inverse sine, System.Numerics.Complex.Asin.</summary>
public sealed record Asin(AstNode Operand) : AstNode;

/// <summary>Complex inverse cosine, System.Numerics.Complex.Acos.</summary>
public sealed record Acos(AstNode Operand) : AstNode;

/// <summary>Complex inverse tangent, System.Numerics.Complex.Atan.</summary>
public sealed record Atan(AstNode Operand) : AstNode;

/// <summary>Complex inverse hyperbolic sine, log(z + sqrt(z²+1)).</summary>
public sealed record Asinh(AstNode Operand) : AstNode;

/// <summary>Complex inverse hyperbolic cosine, log(z + sqrt(z²−1)).</summary>
public sealed record Acosh(AstNode Operand) : AstNode;

/// <summary>Complex inverse hyperbolic tangent, ½·log((1+z)/(1−z)).</summary>
public sealed record Atanh(AstNode Operand) : AstNode;

/// <summary>Principal argument of a complex number, lifted to complex as
/// (arg, 0). arg(a+bi) = atan2(b, a) ∈ (-π, π]. Non-holomorphic — same
/// gating as <see cref="Conj"/>: distance estimate disabled, perturbation
/// Taylor / BLA disabled, SA recurrence rejected. Differentiator treats
/// it as opaque (∂arg/∂z = 0 in the holomorphic chain rule).</summary>
public sealed record Arg(AstNode Operand) : AstNode;

/// <summary>Two-argument arctangent, lifted to complex as (atan2(y, x), 0).
/// Same gating as <see cref="Arg"/>. The unary form `arg(z)` desugars in
/// downstream visitors that prefer the binary surface — emission for both
/// goes through the same OpArg path on the emitter base class.</summary>
public sealed record Atan2(AstNode Y, AstNode X) : AstNode;

/// <summary>Real minimum of two operands, lifted to complex as (min, 0).
/// Inputs are treated as real-valued — the emitter feeds Re(Left) and
/// Re(Right) to the underlying Math.Min. Non-holomorphic (subgradient at
/// the boundary). Distance estimate / perturbation / BLA / SA all gate
/// off via the same hasArg / hasMinMax flag rolled into hasTrans.</summary>
public sealed record Min(AstNode Left, AstNode Right) : AstNode;

/// <summary>Real maximum of two operands, lifted to complex. See <see cref="Min"/>.</summary>
public sealed record Max(AstNode Left, AstNode Right) : AstNode;

/// <summary>Real part lifted to complex: (Re(x), 0). Holomorphic-wise this is
/// non-holomorphic (like <see cref="Conj"/>) — distance estimate / perturbation
/// / SA all gate off. #27 Phase 6 — the expression-position `re(x)` that brings
/// CalcGen to parity with the SandboxExpression interpreter (which has it). Note
/// the SEPARATE condition-only <see cref="CondRe"/> already existed for `if`
/// terms; this is the value-expression form.</summary>
public sealed record ReOp(AstNode Operand) : AstNode;

/// <summary>Imaginary part lifted to complex: (Im(x), 0). Non-holomorphic; same
/// gating as <see cref="ReOp"/>. #27 Phase 6.</summary>
public sealed record ImOp(AstNode Operand) : AstNode;

/// <summary>Magnitude lifted to complex: (|x|, 0) = (sqrt(Re²+Im²), 0).
/// Non-holomorphic; same gating. #27 Phase 6 — SEMANTIC RECONCILIATION: the
/// expression `abs(x)` is |x| (matches the SandboxExpression runtime and
/// Complex.Abs). This is DISTINCT from the condition-only `abs` shorthand
/// (<see cref="CondAbs2"/>) which is |x|² (squared magnitude) — a different
/// grammar position (inside `if`), kept for its bailout-threshold convenience.</summary>
public sealed record AbsOp(AstNode Operand) : AstNode;

/// <summary>Real-valued clamp lifted to complex: (clamp(Re(x), Re(lo), Re(hi)), 0).
/// Matches SandboxExpression's real-valued clamp. Non-holomorphic; same gating as
/// <see cref="Min"/>/<see cref="Max"/>. #27 Phase 6.</summary>
public sealed record Clamp(AstNode X, AstNode Lo, AstNode Hi) : AstNode;

/// <summary>Real modulo (IEEE remainder semantics): mod(a, b) = a - trunc(a/b)*b
/// on the real components, lifted to complex. The emitter uses C#'s `%`
/// on doubles (matches Math.IEEERemainder for finite arguments after the
/// trunc-style rounding the spec prescribes; users who need true IEEE
/// rounding can rewrite explicitly). Same gating as Min / Max.</summary>
public sealed record Mod(AstNode Left, AstNode Right) : AstNode;

/// <summary>Piecewise complex expression: if <paramref name="Cond"/> then
/// <paramref name="Then"/> else <paramref name="Else"/>. Holomorphic
/// piecewise — distance estimate is valid inside each branch but the
/// chain rule has a discontinuity along the locus where Cond changes
/// truth value. Perturbation/BLA/SA disabled when present because the
/// δ-Taylor expansion has no closed form across the branch boundary.
/// </summary>
public sealed record If(CondNode Cond, AstNode Then, AstNode Else) : AstNode;

// Conditional sub-grammar. Lives separately from <see cref="AstNode"/>
// because conditions are boolean-valued (real comparisons) while
// AstNode is complex-valued. Restricting conditions to this small
// grammar keeps the differentiator from ever having to differentiate a
// non-holomorphic boolean operator — the cond stays untouched.

public abstract record CondNode;

/// <summary>Comparison op codes for <see cref="Cmp"/>.</summary>
public enum CmpOp { Gt, Lt, Ge, Le, Eq, Ne }

/// <summary>Real-valued comparison <c>Left op Right</c>. Both sides
/// must be <see cref="CondTerm"/>s, which extract real scalars from
/// complex sub-expressions (Re/Im/Abs2) or carry literal constants.
/// </summary>
public sealed record Cmp(CmpOp Op, CondTerm Left, CondTerm Right) : CondNode;

/// <summary>Real-scalar leaf used inside <see cref="Cmp"/>. Kept as a
/// separate hierarchy from <see cref="AstNode"/> so the differentiator
/// never sees Re/Im/Abs2 nodes (non-holomorphic) — they live only
/// inside condition expressions.</summary>
public abstract record CondTerm;

/// <summary>Real part of a complex sub-expression.</summary>
public sealed record CondRe(AstNode Of) : CondTerm;

/// <summary>Imaginary part of a complex sub-expression.</summary>
public sealed record CondIm(AstNode Of) : CondTerm;

/// <summary>Squared magnitude |x|² = Re(x)² + Im(x)² of a complex
/// sub-expression. Avoids the sqrt of full |x|; sufficient for most
/// inequality conditions and matches the bailout-style threshold form
/// users already think in.</summary>
public sealed record CondAbs2(AstNode Of) : CondTerm;

/// <summary>Principal argument of a complex sub-expression as a real
/// scalar: <c>arg(x) = atan2(Im(x), Re(x)) ∈ (-π, π]</c>. Lets users
/// branch by orbit phase, e.g. <c>if arg(z) &gt; 0 then z*z + c else z*z - c</c>.
/// Lives only inside conditions — the regular <see cref="Arg"/> AstNode
/// already handles arg as a complex value in normal expression
/// position. Non-holomorphic, but conditions don't feed the
/// differentiator chain so this doesn't affect DE gating.</summary>
public sealed record CondArg(AstNode Of) : CondTerm;

/// <summary>Real literal inside a comparison.</summary>
public sealed record CondConst(double Value) : CondTerm;
