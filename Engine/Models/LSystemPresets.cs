using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>
    /// Definition of an L-system: axiom (start string), production rules, turn
    /// angle (degrees), and an optional default iteration depth.
    /// </summary>
    public sealed record LSystemDefinition(
        string Axiom,
        Dictionary<char, string> Rules,
        double AngleDegrees,
        int DefaultDepth,
        double StartAngleDegrees = 0.0);

    public static class LSystemPresets
    {
        public static readonly Dictionary<string, LSystemDefinition> All = new()
        {
            ["Hilbert"] = new LSystemDefinition(
                "A",
                new() {
                    { 'A', "-BF+AFA+FB-" },
                    { 'B', "+AF-BFB-FA+" }
                },
                90.0,
                5),

            ["Koch Snowflake"] = new LSystemDefinition(
                "F++F++F",
                new() { { 'F', "F-F++F-F" } },
                60.0,
                4),

            ["Sierpinski Arrowhead"] = new LSystemDefinition(
                "A",
                new() {
                    { 'A', "B-A-B" },
                    { 'B', "A+B+A" },
                },
                60.0,
                6),

            ["Dragon"] = new LSystemDefinition(
                "FX",
                new() {
                    { 'X', "X+YF+" },
                    { 'Y', "-FX-Y" },
                },
                90.0,
                10),

            ["Plant"] = new LSystemDefinition(
                "X",
                new() {
                    { 'X', "F+[[X]-X]-F[-FX]+X" },
                    { 'F', "FF" },
                },
                25.0,
                5,
                StartAngleDegrees: 65.0),

            ["Gosper"] = new LSystemDefinition(
                "A",
                new() {
                    { 'A', "A-B--B+A++AA+B-" },
                    { 'B', "+A-BB--B-A++A+B" },
                },
                60.0,
                4),

            ["Pythagoras Tree"] = new LSystemDefinition(
                "A",
                new() {
                    { 'A', "B[+A]-A" },
                    { 'B', "BB" },
                },
                45.0,
                7,
                StartAngleDegrees: 90.0),

            ["Koch Curve"] = new LSystemDefinition(
                "F",
                new() { { 'F', "F+F--F+F" } },
                60.0,
                4),

            ["Peano"] = new LSystemDefinition(
                "X",
                new() {
                    { 'X', "XFYFX+F+YFXFY-F-XFYFX" },
                    { 'Y', "YFXFY-F-XFYFX+F+YFXFY" },
                },
                90.0,
                3),

            ["Levy C"] = new LSystemDefinition(
                "F",
                new() { { 'F', "+F--F+" } },
                45.0,
                12),

            ["Pentigree"] = new LSystemDefinition(
                "F",
                new() { { 'F', "+F++F----F--F++F++F-" } },
                36.0,
                4),
        };
    }
}
