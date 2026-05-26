// Hosting/HostHelpContentProvider.cs
//
// Minimal IHelpContentProvider for the Avalonia shell. The full FloatingHelp
// in the legacy WinForms project hard-codes ~2,500 lines of static help text
// in private string builders; the long-term plan is to extract those into
// shared resources both shells can read. Until then, this provider returns
// the program name + version plus a short placeholder for each tab so the
// Avalonia shell is usable while the heavy text migration is queued as a
// separate task.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

using FracturingFog.Help;

namespace FracturingFog.Hosting
{
    /// <inheritdoc/>
    public sealed class HostHelpContentProvider : IHelpContentProvider
    {
        public string ProgramName => "Fracturing Fog";

        public string ProgramVersion
        {
            get
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                return v != null
                    ? $"{v.Major}.{v.Minor}.{v.Build}"
                    : "(unknown)";
            }
        }

        public string AboutText =>
            "Fracturing Fog — interactive fractal explorer.\n\n" +
            "This Avalonia shell is the Phase 2 cross-platform host. The full " +
            "help text from the legacy WinForms FloatingHelp will be wired in " +
            "once it has been extracted into a shared resource bundle.";

        public string FeaturesText =>
            "• 12 fractal calculators (Mandelbrot, Julia, Newton, Mandelbulb, etc.)\n" +
            "• Quad-precision arithmetic (~62 digits) at deep zoom\n" +
            "• Data-driven colour themes with JSON import / export\n" +
            "• PBR + Phong 3D lighting for raymarched fractals\n" +
            "• Histogram-equalised adaptive contrast\n" +
            "• Slideshow + video-zoom recording";

        public string AudioText =>
            "Audio sonification reads the active fractal's escape pattern as " +
            "a polyphonic waveform. See the Audio Settings dialog for routing " +
            "and the AudioSettings tab in the Floating Menu for live controls.";

        public string EditorText =>
            "The Color Theme Editor is modeless — leave it open while panning " +
            "the main view to tune gradients against the live image. Preview " +
            "updates are debounced 150 ms so dragging stops feel snappy.";

        public string BioText =>
            "Maintained by Bradley Brimhall + Claude. Source: see repository " +
            "PHASE2_AVALONIA_MIGRATION.md for current cross-platform status.";

        public IReadOnlyList<HelpSubTab> MathSubTabs { get; } = new HelpSubTab[]
        {
            new("Mandelbrot",
                "z[n+1] = z[n]^2 + c.  The set is the locus of points c for " +
                "which the orbit of 0 stays bounded."),
            new("Julia",
                "Same recurrence, but c is fixed and z varies. Each c yields " +
                "a different Julia set; the Mandelbrot set is the c-parameter " +
                "atlas."),
            new("Newton",
                "Newton's method applied to a polynomial. Pixels are coloured " +
                "by which root the iteration converges to and how quickly."),
            new("Mandelbulb",
                "3D analogue using triplex (r, θ, φ) algebra. Distance " +
                "estimation drives the raymarcher; colour comes from depth + " +
                "normal."),
        };

        public IReadOnlyList<HelpLink> AboutLinks { get; } = new HelpLink[]
        {
            new("Project repository",
                "https://github.com/anthropics/anthropic-cookbook"),
        };

        public string GetSystemInfoText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"OS              : {Environment.OSVersion}");
            sb.AppendLine($"Runtime         : {Environment.Version}");
            sb.AppendLine($"Processors      : {Environment.ProcessorCount}");
            sb.AppendLine($"Working set     : {Environment.WorkingSet / (1024 * 1024)} MB");
            sb.AppendLine();
            sb.AppendLine("(DXGI / D3D11 adapter enumeration will be wired in once");
            sb.AppendLine("the legacy FloatingHelp builders are extracted into a");
            sb.AppendLine("shared helper. See PHASE2_AVALONIA_MIGRATION.md.)");
            return sb.ToString();
        }
    }
}
