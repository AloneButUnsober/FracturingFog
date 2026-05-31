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
