// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlane3DPresets.cs
//
// #1719 (Math Plane 3D T7) — the built-in "math behind the 3D fractals" demos. Each one is
// a scene plus the Math Plane 3D map, camera and zoom, and names the idea it shows. They are
// read-only: loading one copies it into the view's params; editing afterwards never changes
// the built-in. Design: MathPlane3D-DesignPlan.md §1, §9.
//
// Batch parity: ParamArgs() is the --param list (the region-snapshot keys; the orbit camera
// is the shared Cam3DDistance / Cam3DTheta / Cam3DPhi), and Zoom the view, so every demo
// renders headless as
//   FracturingFog --batch --fractal MathPlane3D --x 0 --y 0 --zoom Z --param ... --out a.png
//
// The ideas are standard linear algebra and surface geometry. What they point at in FF's
// 3D fractals:
//   - polar vs linear frames: Higham's polar decomposition + Shoemake's slerp (T3,
//     bibliography #higham-polar-decomposition, #shoemake-slerp);
//   - the KIFS fold: FF's Sierpinski fold "if (x + y < 0) (x, y) = (-y, -x)"
//     (KifsCalculator.SierpDE), after Knighty's Kaleidoscopic IFS (fractalforums.com, 2010);
//   - the Mandelbox's scale step z -> s z + c (default s = 2), after Tom Lowe's Mandelbox
//     (fractalforums.com, 2010);
//   - the grid-of-space look: 3Blue1Brown, Essence of linear algebra ch. 5
//     (#sanderson-3d-transformations).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace FracturingFog.Models
{
    /// <summary>#1719 — one built-in Math Plane 3D demo.</summary>
    public sealed record MathPlane3DPreset(
        string Name,
        string Idea,
        string Scene,
        bool MapOn = false,
        Mat3? Matrix = null,
        double T = 1,
        MathPlaneInterpolation Interpolation = MathPlaneInterpolation.Linear,
        double Distance = 9.0, double Theta = 0.9, double Phi = 1.1,
        double Zoom = 1.0)
    {
        /// <summary>The map's matrix (identity when none is given).</summary>
        public Mat3 M => Matrix ?? Mat3.Identity;

        /// <summary>Copy the demo into <paramref name="p"/>: every Math Plane 3D param is set,
        /// so nothing from the previous scene carries over.</summary>
        public void Apply(FractalParameters p)
        {
            var m = M;
            p.MathPlane3DScene = Scene;
            p.MathPlane3DMapOn = MapOn;
            (p.MathPlane3DM11, p.MathPlane3DM12, p.MathPlane3DM13) = (m.M11, m.M12, m.M13);
            (p.MathPlane3DM21, p.MathPlane3DM22, p.MathPlane3DM23) = (m.M21, m.M22, m.M23);
            (p.MathPlane3DM31, p.MathPlane3DM32, p.MathPlane3DM33) = (m.M31, m.M32, m.M33);
            p.MathPlane3DT = T;
            p.MathPlane3DInterpolation = Interpolation;
            (p.MathPlane3DCameraDistance, p.MathPlane3DCameraTheta, p.MathPlane3DCameraPhi) = (Distance, Theta, Phi);
            (p.MathPlane3DTargetX, p.MathPlane3DTargetY, p.MathPlane3DTargetZ) = (0, 0, 0);
        }

        /// <summary>The <c>--param KEY=VALUE</c> values that reproduce the demo.</summary>
        public IReadOnlyList<string> ParamArgs()
        {
            var inv = CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("R", inv);
            var m = M;
            return new[]
            {
                "MathPlane3DScene=" + Scene,
                "MathPlane3DMapOn=" + (MapOn ? "true" : "false"),
                "MathPlane3DM11=" + N(m.M11), "MathPlane3DM12=" + N(m.M12), "MathPlane3DM13=" + N(m.M13),
                "MathPlane3DM21=" + N(m.M21), "MathPlane3DM22=" + N(m.M22), "MathPlane3DM23=" + N(m.M23),
                "MathPlane3DM31=" + N(m.M31), "MathPlane3DM32=" + N(m.M32), "MathPlane3DM33=" + N(m.M33),
                "MathPlane3DT=" + N(T),
                "MathPlane3DInterpolation=" + ((int)Interpolation).ToString(inv),
                "Cam3DDistance=" + N(Distance), "Cam3DTheta=" + N(Theta), "Cam3DPhi=" + N(Phi),
                "MathPlane3DTargetX=0", "MathPlane3DTargetY=0", "MathPlane3DTargetZ=0",
            };
        }
    }

    public static class MathPlane3DPresets
    {
        private const string Basis = "vector i 1 0 0 #E69F00; vector j 0 1 0 #56B4E9; vector k 0 0 1 #009E73";

        /// <summary>A quarter turn about x, y and z (right-handed).</summary>
        public static readonly Mat3 RotX = new(1, 0, 0, 0, 0, -1, 0, 1, 0);
        public static readonly Mat3 RotY = new(0, 0, 1, 0, 1, 0, -1, 0, 0);
        public static readonly Mat3 RotZ = new(0, -1, 0, 1, 0, 0, 0, 0, 1);

        /// <summary>FF's Sierpinski KIFS fold as a matrix: the reflection in the plane x + y = 0,
        /// (x, y, z) -> (-y, -x, z).</summary>
        public static readonly Mat3 SierpinskiFold = new(0, -1, 0, -1, 0, 0, 0, 0, 1);

        /// <summary>The Mandelbox's default scale step, s = 2.</summary>
        public static readonly Mat3 Scale2 = new(2, 0, 0, 0, 2, 0, 0, 0, 2);

        /// <summary>A stretch-and-shear with det 2 · 1 · 1.5 = 3.</summary>
        public static readonly Mat3 Det3 = new(2, 1, 0, 0, 1, 0, 0, 0.5, 1.5);

        /// <summary>The built-in demos, in teaching order.</summary>
        public static IReadOnlyList<MathPlane3DPreset> All { get; } = new MathPlane3DPreset[]
        {
            // ── Rotations: the frames of a turning fractal ──
            new("Rotation about z: polar, halfway",
                "A quarter turn about z, stopped at t = 1/2. Polar frames turn the rotation's angle, so i and j have "
                + "turned 45 degrees and keep length 1; k is on the axis and does not move.",
                "lengths on; eigen on; " + Basis,
                MapOn: true, Matrix: RotZ, T: 0.5, Interpolation: MathPlaneInterpolation.Polar),
            new("Rotation about z: linear, halfway",
                "The same quarter turn at t = 1/2 with linear frames: I + t (R - I) is not a rotation. i and j point "
                + "the same way as with polar frames but shrink to length 0.71 (det 1/2). Polar frames keep a turn rigid.",
                "lengths on; det on; " + Basis,
                MapOn: true, Matrix: RotZ, T: 0.5, Interpolation: MathPlaneInterpolation.Linear),
            new("Rotation about x",
                "A quarter turn about x: j goes to k and k goes to -j. x is the only real eigenvector (eigenvalue 1); "
                + "det is 1, so volumes are kept. The 3D fractals' camera and their rotated folds turn space like this.",
                "eigen on; det on; " + Basis,
                MapOn: true, Matrix: RotX, Interpolation: MathPlaneInterpolation.Polar),
            new("Rotation about y",
                "A quarter turn about y: k goes to i and i goes to -k. Columns of the matrix are where i, j and k land; "
                + "y is the axis. Play t to see the turn.",
                "eigen on; det on; " + Basis,
                MapOn: true, Matrix: RotY, Interpolation: MathPlaneInterpolation.Polar),

            // ── The folds and scale of the 3D fractals ──
            new("KIFS fold: a reflection",
                "The Sierpinski KIFS fold, if x + y < 0 then (x, y) -> (-y, -x), is the reflection in the plane "
                + "x + y = 0 applied to the half of space behind it. As a matrix: det -1 (the cube turns inside out), "
                + "eigenvalue -1 across the mirror and 1 along it. The point p behind the mirror lands in front.",
                "det on; eigen on; " + Basis + "; vector p -1.5 0.5 1 #CC79A7; "
                + "surface mirror parametric \"u\" \"-u\" \"v\" -2.5 2.5 -1.5 1.5 #9A9A9A60 nolabel mesh 8",
                MapOn: true, Matrix: SierpinskiFold),
            new("Uniform scale: the Mandelbox step",
                "Each Mandelbox iteration ends with z -> s z + c, s = 2 by default: every length doubles and every "
                + "volume grows 2^3 = 8 times (det 8). Every direction is an eigenvector with eigenvalue 2.",
                "det on; lengths on; " + Basis,
                MapOn: true, Matrix: Scale2, Distance: 12),
            new("det is the volume of the cube's image",
                "The unit cube's image is the parallelepiped spanned by the columns (2, 0, 0), (1, 1, 0.5), (0, 0, 1.5). "
                + "Its volume is |det| = 3; the sign says whether the cube turned inside out.",
                "det on; " + Basis,
                MapOn: true, Matrix: Det3, Distance: 10),

            // ── Surfaces ──
            new("Saddle z = x^2 - y^2",
                "Along x the surface curves up (z = x^2), along y it curves down (z = -y^2): the origin is a saddle "
                + "point, a minimum one way and a maximum the other. Its curvature there is negative.",
                "surface s explicit \"x^2 - y^2\" -1.6 1.6 -1.6 1.6 #56B4E9 mesh 32; "
                + "curve up \"t\" \"0\" \"t^2\" -1.6 1.6 #E69F00; curve down \"0\" \"t\" \"-t^2\" -1.6 1.6 #009E73",
                Distance: 10),
            new("Parametric torus",
                "A circle of radius 0.6 swept round a circle of radius 2: (x, y, z) = ((2 + 0.6 cos v) cos u, "
                + "(2 + 0.6 cos v) sin u, 0.6 sin v) for u, v in [0, 2 pi]. u goes round the ring, v round the tube.",
                "surface torus parametric \"(2 + 0.6*cos(v))*cos(u)\" \"(2 + 0.6*cos(v))*sin(u)\" \"0.6*sin(v)\" "
                + "0 6.283185307179586 0 6.283185307179586 #E69F00 mesh 48",
                Distance: 10),

            // ── #1736 (bridge B6) — the 3D fractals themselves, with orbits under their own step ──
            // Orbit numbers below are in the fractal's own axes (y up), as the fractal views use;
            // the scene shows them turned to z up, (x, y, z) -> (x, -z, y). Each is checked
            // against MathPlane3DOrbit by MathPlane3DBridgeDemoTests.
            new("Mandelbulb: an escaping orbit",
                "The power-8 Mandelbulb as a mesh, and the orbit of one point under its own step: the triplex power, "
                + "then plus the point. The orbit stays within |z| of about 1.2 for three steps, then jumps to |z| = 4.9 "
                + "and escapes at step 4 (|z| > 2). A point is in the Mandelbulb when its orbit never does.",
                "fractal bulb Mandelbulb #56B4E980 nolabel mesh 64; orbit o Mandelbulb 0.6 0.4 0.5 #E69F00",
                Distance: 4.5),
            new("Mandelbox: fold, then scale",
                "One Mandelbox step is a box fold (a coordinate past 1 is reflected back), a sphere fold, a scale by s = 2, "
                + "then plus the start point c. From (0.9, 0.6, -0.3): step 1 folds nothing (inside the box, outside the "
                + "unit sphere), so it only scales and adds c: (2.7, 1.8, -0.9). Step 2 folds x = 2.7 and y = 1.8 back to "
                + "-0.7 and 0.2, then scales and adds c: (-0.5, 1, -2.1). The grey cube is the fold box |x|, |y|, |z| <= 1 "
                + "and the green ball the unit sphere (the sphere fold's fixed radius); the s = 2 Mandelbox itself is a "
                + "solid block filling its 12-wide meshing cube, which would hide them, so it is left out.",
                "surface sphere implicit \"x^2 + y^2 + z^2 - 1\" -1.2 1.2 -1.2 1.2 -1.2 1.2 #009E7340 nolabel mesh 32; "
                + "polyline box -1 -1 -1 1 -1 -1 1 1 -1 -1 1 -1 -1 -1 -1 -1 -1 1 1 -1 1 1 1 1 -1 1 1 -1 -1 1 #9A9A9A nolabel; "
                + "segment e1 1 -1 -1 1 -1 1 #9A9A9A nolabel; segment e2 1 1 -1 1 1 1 #9A9A9A nolabel; "
                + "segment e3 -1 1 -1 -1 1 1 #9A9A9A nolabel; "
                + "orbit o Mandelbox 0.9 0.3 0.6 steps 3 #E69F00",
                Distance: 9),
            new("KIFS fold step beside the mesh",
                "The Sierpinski KIFS step: three mirror folds (x + y < 0, x + z < 0, y + z < 0), then z -> 2 z - (1, 1, 1), "
                + "a scale by 2 about the corner (1, 1, 1). The point (-0.8, 0.3, 0.6) is behind the x + y = 0 mirror, so the "
                + "fold reflects it to (-0.3, 0.8, 0.6), and the scale sends it to (-1.6, 0.6, 0.2). Points of the "
                + "tetrahedron stay bounded under these steps; this one is pushed away.",
                "fractal tet Kifs KifsFold=Sierpinski #56B4E980 nolabel mesh 64; "
                + "orbit o Kifs -0.8 -0.6 0.3 steps 2 KifsFold=Sierpinski #E69F00",
                Distance: 6),
            new("Quaternion Julia slice and its orbit",
                "The quaternion Julia set of q^2 + c, c = (-0.2, 0.4, -0.4, -0.4), sliced at w = 0, and the orbit of a point "
                + "on the slice. Quaternions are 4D, so the scene draws (x, y, z) and drops w: c's w = -0.4 moves the orbit "
                + "off the slice at the first step. It wanders for six steps and escapes at step 7.",
                "fractal julia QuaternionJulia #56B4E980 nolabel mesh 64; orbit o QuaternionJulia 0.3 0.2 0.4 #E69F00",
                Distance: 4.5),
        };

        /// <summary>The demo named <paramref name="name"/> (ordinal), or null.</summary>
        public static MathPlane3DPreset? Find(string? name)
        {
            foreach (var d in All) if (string.Equals(d.Name, name, StringComparison.Ordinal)) return d;
            return null;
        }
    }
}
