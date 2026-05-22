using System;
using System.Collections.Generic;
using System.Numerics;

namespace FracturingFog.Models
{
    /// <summary>
    /// Per-fractal parameters carried by MainForm and passed to the appropriate
    /// calculator. Only the fields relevant to the active FractalType are read.
    /// </summary>
    public sealed class FractalParameters
    {
        public Complex JuliaC { get; set; } = new Complex(-0.7, 0.27015);

        public int MultibrotExponent { get; set; } = 3;

        public Complex PhoenixP { get; set; } = new Complex(0.56667, 0.0);

        public Complex[]? NewtonPolyCoeffs { get; set; }

        public List<AffineMap>? IFSMaps { get; set; }

        public string? UserEquationSource { get; set; }

        /// <summary>
        /// Name of the saved <see cref="UserEquationEntry"/> the current source
        /// came from. Null when the user has typed a custom equation that doesn't
        /// match any saved entry. Region save/recall uses this to round-trip
        /// the equation by reference (name) rather than copying source into JSON.
        /// </summary>
        public string? UserEquationName { get; set; }

        /// <summary>
        /// View rotation applied to the UserEquation parameter plane, in degrees
        /// (CCW). Rotates the (dx, dy) pixel offset before adding to center so the
        /// rendered fractal appears tilted. 0 = unrotated.
        /// </summary>
        public double UserEquationRotationDegrees { get; set; } = 0.0;

        /// <summary>
        /// Source for the Sandbox fractal — a restricted expression DSL parsed by
        /// <see cref="SandboxExpression"/>. Safe to evaluate in untrusted contexts:
        /// no BCL access, no IO, no reflection.
        /// </summary>
        public string? SandboxSource { get; set; }

        /// <summary>
        /// Name of the saved <see cref="SandboxEquationEntry"/> the current source
        /// came from. Round-tripped by region save/recall the same way
        /// <see cref="UserEquationName"/> is.
        /// </summary>
        public string? SandboxName { get; set; }

        public string IFSPresetName { get; set; } = "Sierpinski Triangle";
        public int IFSIterations { get; set; } = 2_000_000;

        public string LSystemPresetName { get; set; } = "Hilbert";
        public int LSystemDepth { get; set; } = 5;

        public string AttractorPresetName { get; set; } = "Clifford";
        public int AttractorIterations { get; set; } = 2_000_000;
        public double AttractorA { get; set; } = -1.4;
        public double AttractorB { get; set; } = 1.6;
        public double AttractorC { get; set; } = 1.0;
        public double AttractorD { get; set; } = 0.7;

        public int NewtonExponent { get; set; } = 3;
        public double NewtonRelaxation { get; set; } = 1.0;

        public int BuddhaSamples { get; set; } = 500_000;
        public int BuddhaIterLow { get; set; } = 500;
        public int BuddhaIterMid { get; set; } = 5_000;
        public int BuddhaIterHigh { get; set; } = 50_000;

        // Mandelbulb camera + DE settings.
        public double BulbPower { get; set; } = 8.0;
        public int BulbIterations { get; set; } = 8;
        public double BulbCameraDistance { get; set; } = 3.0;
        public double BulbCameraTheta { get; set; } = Math.PI * 0.25;  // azimuth (around Y)
        public double BulbCameraPhi { get; set; } = Math.PI * 0.35;    // elevation
        public double BulbLightTheta { get; set; } = Math.PI * 0.25;
        public double BulbLightPhi { get; set; } = Math.PI * 0.45;
        public int BulbMaxSteps { get; set; } = 96;
        public double BulbEpsilon { get; set; } = 0.0015;

        public FractalParameters Clone()
        {
            return new FractalParameters
            {
                JuliaC = JuliaC,
                MultibrotExponent = MultibrotExponent,
                PhoenixP = PhoenixP,
                NewtonPolyCoeffs = NewtonPolyCoeffs is null ? null : (Complex[])NewtonPolyCoeffs.Clone(),
                IFSMaps = IFSMaps is null ? null : new List<AffineMap>(IFSMaps),
                UserEquationSource = UserEquationSource,
                UserEquationName = UserEquationName,
                UserEquationRotationDegrees = UserEquationRotationDegrees,
                SandboxSource = SandboxSource,
                SandboxName = SandboxName,
                IFSPresetName = IFSPresetName,
                IFSIterations = IFSIterations,
                LSystemPresetName = LSystemPresetName,
                LSystemDepth = LSystemDepth,
                AttractorPresetName = AttractorPresetName,
                AttractorIterations = AttractorIterations,
                AttractorA = AttractorA, AttractorB = AttractorB,
                AttractorC = AttractorC, AttractorD = AttractorD,
                NewtonExponent = NewtonExponent,
                NewtonRelaxation = NewtonRelaxation,
                BuddhaSamples = BuddhaSamples,
                BuddhaIterLow = BuddhaIterLow,
                BuddhaIterMid = BuddhaIterMid,
                BuddhaIterHigh = BuddhaIterHigh,
                BulbPower = BulbPower,
                BulbIterations = BulbIterations,
                BulbCameraDistance = BulbCameraDistance,
                BulbCameraTheta = BulbCameraTheta,
                BulbCameraPhi = BulbCameraPhi,
                BulbLightTheta = BulbLightTheta,
                BulbLightPhi = BulbLightPhi,
                BulbMaxSteps = BulbMaxSteps,
                BulbEpsilon = BulbEpsilon
            };
        }
    }

    /// <summary>
    /// Affine map for IFS chaos game. x' = a·x + b·y + e, y' = c·x + d·y + f. Picked with weight.
    /// </summary>
    public readonly record struct AffineMap(double A, double B, double C, double D, double E, double F, double Weight);
}
