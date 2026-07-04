// Models/UserBulbSnapshot.cs
//
// Versioned export envelope for a UserBulb equation. Wraps a UserBulbEntry
// (Name/Source/Promoted/Chain) plus the runtime knobs that materially affect
// what the equation renders — axis mode, Julia params, camera, lights,
// colour driver, render budget, view, animation, named params.
//
// Used by UserBulbStore.ExportSnapshot / ImportSnapshot to write/read .fbulb
// files. Every knob is nullable so a snapshot that omits a field leaves the
// corresponding FractalParameters slot untouched on import — keeps the format
// forward-compatible (older readers ignore unknown fields, newer fields
// applied opportunistically by newer readers).
//
// Legacy fallback: ImportSnapshot detects a bare UserBulbEntry JSON (no
// Version field) and constructs a snapshot with only Entry populated, so
// pre-Wave-4.13 .fbulb files round-trip without breaking.

using System.Collections.Generic;

namespace FracturingFog.Models
{
    public sealed class UserBulbSnapshot
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public UserBulbEntry Entry { get; set; } = new();

        // ── Axis / compiler ────────────────────────────────────────────
        public UserBulbAxisModeKind? AxisMode { get; set; }
        public UserBulbCompilerKind? Compiler { get; set; }
        public UserBulbDEModeKind? DEMode { get; set; }
        public UserBulbBackendKind? Backend { get; set; }
        public double? QuatSliceW { get; set; }

        // ── Julia ──────────────────────────────────────────────────────
        public bool? JuliaMode { get; set; }
        public double? JuliaCX { get; set; }
        public double? JuliaCY { get; set; }
        public double? JuliaCZ { get; set; }
        public double? JuliaCW { get; set; }

        // ── Camera + lights ────────────────────────────────────────────
        public double? CameraDistance { get; set; }
        public double? CameraTheta { get; set; }
        public double? CameraPhi { get; set; }
        public double? LightTheta { get; set; }
        public double? LightPhi { get; set; }
        public double? Light1Intensity { get; set; }
        public double? Light2Intensity { get; set; }
        public double? Light3Intensity { get; set; }
        public int? AOSamples { get; set; }
        public double? FogDensity { get; set; }

        // ── Colour driver ──────────────────────────────────────────────
        public BulbColorDriver? ColorDriver { get; set; }
        public double? OrbitTrapX { get; set; }
        public double? OrbitTrapY { get; set; }
        public double? OrbitTrapZ { get; set; }
        public int? IterComponentAxis { get; set; }

        // ── Render budget ──────────────────────────────────────────────
        public int? Iterations { get; set; }
        public int? MaxSteps { get; set; }
        public double? Epsilon { get; set; }
        public double? Bailout { get; set; }
        public double? JacobianH { get; set; }
        public double? CullRadius { get; set; }

        // ── View ───────────────────────────────────────────────────────
        public double? FovDegrees { get; set; }
        public bool? ClipPlaneEnabled { get; set; }
        public int? SuperSample { get; set; }

        // ── Animation ──────────────────────────────────────────────────
        public double? Time { get; set; }

        // ── Named params (UserBulbParam.Value / Min / Max) ─────────────
        public List<UserBulbParam>? Params { get; set; }
    }
}
