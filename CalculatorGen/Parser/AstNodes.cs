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

/// <summary>Real-valued numeric literal. Treated as complex (n, 0).</summary>
public sealed record RealConst(double Value) : AstNode;

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
