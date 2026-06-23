// Hosting/HostHelpContentProvider.cs
//
// IHelpContentProvider for the Avalonia shell. Live system info (program name
// + version, DXGI adapters, D3D11 feature level, CPU / OS / memory) is gathered
// here; all long-form tab prose is read from the shared HelpTextBundle in the
// Abstractions assembly, so the Avalonia FloatingHelp window renders the same
// full text the WinForms FloatingHelp has shipped for years.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using FracturingFog.Help;

namespace FracturingFog.Hosting
{
    /// <inheritdoc/>
    public sealed class HostHelpContentProvider : IHelpContentProvider
    {
        // Phase X.0 / Slice 0.3b — optional OS-specific GPU info source.
        // Host bootstrap installs WindowsD3D11HardwareInfoProvider on
        // Windows; non-Win hosts leave it null and the system info text
        // shows a friendly "not available" note.
        private readonly IHardwareInfoProvider? _hardwareInfo;

        public HostHelpContentProvider(IHardwareInfoProvider? hardwareInfo = null)
        {
            _hardwareInfo = hardwareInfo;
        }

        // Phase X.5 / Slice 5.2 — host-supplied probes for the new Hardware tab
        // sections. Bootstrap sets these once after the audio backend +
        // ILGPU context come up; left null on hosts that have not populated
        // them so the Hardware text degrades to a friendly placeholder rather
        // than crashing on a null deref.
        public static Func<string?>? AudioBackendProbe { get; set; }
        public static Func<string?>? IlgpuDeviceProbe { get; set; }

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

        public string AboutText    => HelpTextBundle.AboutText;
        public string FeaturesText => HelpTextBundle.FeaturesText;
        public string BatchText    => HelpTextBundle.BatchText;
        public string AudioText    => HelpTextBundle.AudioText;
        public string EditorText   => HelpTextBundle.EditorText;
        public string BioText      => HelpTextBundle.BioText;
        public string ClientServerText => HelpTextBundle.ClientServerText;
        public string CalcGenText      => HelpTextBundle.CalcGenText;
        public string ColorGenText     => HelpTextBundle.ColorGenText;
        public string ToolbarText      => HelpTextBundle.ToolbarText;
        public string RegionsText      => HelpTextBundle.RegionsText;
        public string SlideshowText    => HelpTextBundle.SlideshowText;
        public string ServerAdminText  => HelpTextBundle.ServerAdminText;
        public string PosterText       => HelpTextBundle.PosterText;
        public string ArchitectureText => HelpTextBundle.ArchitectureText;

        // Wave 5.4 — two-level grouping. The flat list below stays in the
        // historical (WinForms) order for back-compat consumers. The grouped
        // view (MathSubTabGroups) clusters by family and is what the Avalonia
        // FloatingHelp window renders.
        public IReadOnlyList<HelpSubTab> MathSubTabs { get; } = new HelpSubTab[]
        {
            new("Overview",      HelpTextBundle.MathOverviewText),
            new("Mandelbrot",    HelpTextBundle.MathMandelbrotText),
            new("Julia",         HelpTextBundle.MathJuliaText),
            new("Burning Ship",  HelpTextBundle.MathBurningShipText),
            new("Tricorn",       HelpTextBundle.MathTricornText),
            new("Multibrot",     HelpTextBundle.MathMultibrotText),
            new("Phoenix",       HelpTextBundle.MathPhoenixText),
            new("Newton",        HelpTextBundle.MathNewtonText),
            new("Nova",          HelpTextBundle.MathNovaText),
            new("Buddhabrot",    HelpTextBundle.MathBuddhabrotText),
            new("IFS",           HelpTextBundle.MathIFSText),
            new("L-System",      HelpTextBundle.MathLSystemText),
            new("Attractor",     HelpTextBundle.MathAttractorText),
            new("Mandelbulb",    HelpTextBundle.MathMandelbulbText),
            new("User Equation", HelpTextBundle.MathUserEquationText),
            new("User Bulb 3D",  HelpTextBundle.MathUserBulbText),
            new("Sandbox",       HelpTextBundle.MathSandboxText),
            new("Mandelbrot Z² (Generated)", HelpTextBundle.MathGeneratedZ2Text),
            new("Magnet 1",      HelpTextBundle.MathMagnetOneText),
            new("Magnet 2",      HelpTextBundle.MathMagnetTwoText),
            new("Glynn",         HelpTextBundle.MathGlynnText),
            new("Logistic",      HelpTextBundle.MathLogisticText),
            new("Halley",        HelpTextBundle.MathHalleyText),
            new("Secant",        HelpTextBundle.MathSecantText),
            new("Spider",        HelpTextBundle.MathSpiderText),
            new("Mandelbox",     HelpTextBundle.MathMandelboxText),
            new("KIFS",          HelpTextBundle.MathKifsText),
            new("Quaternion Julia", HelpTextBundle.MathQuatJuliaText),
            new("Quaternion Mandelbrot", HelpTextBundle.MathQuatMandelbrotText),
            new("Plasma",        HelpTextBundle.MathPlasmaText),
            new("Flame",         HelpTextBundle.MathFlameText),
            new("Apollonian",    HelpTextBundle.MathApollonianText),
            new("Kleinian",      HelpTextBundle.MathKleinianText),
            new("Bicomplex Mandelbrot", HelpTextBundle.MathBicomplexText),
            new("DLA",           HelpTextBundle.MathDlaText),
        };

        public IReadOnlyList<HelpSubTabGroup> MathSubTabGroups { get; } = new HelpSubTabGroup[]
        {
            new("Overview", new HelpSubTab[]
            {
                new("Overview", HelpTextBundle.MathOverviewText),
            }),
            new("2D escape-time", new HelpSubTab[]
            {
                new("Mandelbrot",    HelpTextBundle.MathMandelbrotText),
                new("Julia",         HelpTextBundle.MathJuliaText),
                new("Burning Ship",  HelpTextBundle.MathBurningShipText),
                new("Tricorn",       HelpTextBundle.MathTricornText),
                new("Multibrot",     HelpTextBundle.MathMultibrotText),
                new("Phoenix",       HelpTextBundle.MathPhoenixText),
                new("Newton",        HelpTextBundle.MathNewtonText),
                new("Nova",          HelpTextBundle.MathNovaText),
                new("Magnet 1",      HelpTextBundle.MathMagnetOneText),
                new("Magnet 2",      HelpTextBundle.MathMagnetTwoText),
                new("Glynn",         HelpTextBundle.MathGlynnText),
                new("Halley",        HelpTextBundle.MathHalleyText),
                new("Secant",        HelpTextBundle.MathSecantText),
                new("Spider",        HelpTextBundle.MathSpiderText),
            }),
            new("Histogram", new HelpSubTab[]
            {
                new("Buddhabrot",    HelpTextBundle.MathBuddhabrotText),
                new("Logistic",      HelpTextBundle.MathLogisticText),
            }),
            new("Procedural", new HelpSubTab[]
            {
                new("IFS",           HelpTextBundle.MathIFSText),
                new("L-System",      HelpTextBundle.MathLSystemText),
                new("Attractor",     HelpTextBundle.MathAttractorText),
                new("Plasma",        HelpTextBundle.MathPlasmaText),
                new("Flame",         HelpTextBundle.MathFlameText),
                new("Apollonian",    HelpTextBundle.MathApollonianText),
                new("DLA",           HelpTextBundle.MathDlaText),
            }),
            new("3D + 4D", new HelpSubTab[]
            {
                new("Mandelbulb",    HelpTextBundle.MathMandelbulbText),
                new("Mandelbox",     HelpTextBundle.MathMandelboxText),
                new("KIFS",          HelpTextBundle.MathKifsText),
                new("Quaternion Julia", HelpTextBundle.MathQuatJuliaText),
                new("Quaternion Mandelbrot", HelpTextBundle.MathQuatMandelbrotText),
                new("Bicomplex Mandelbrot", HelpTextBundle.MathBicomplexText),
                new("Kleinian",      HelpTextBundle.MathKleinianText),
            }),
            new("Authoring", new HelpSubTab[]
            {
                new("User Equation", HelpTextBundle.MathUserEquationText),
                new("Sandbox",       HelpTextBundle.MathSandboxText),
                new("User Bulb 3D",  HelpTextBundle.MathUserBulbText),
            }),
            new("Generated", new HelpSubTab[]
            {
                new("Mandelbrot Z² (Generated)", HelpTextBundle.MathGeneratedZ2Text),
            }),
        };

        public IReadOnlyList<HelpLink> AboutLinks { get; } = new HelpLink[]
        {
            new("Mandelbrot set (Wikipedia)",
                "https://en.wikipedia.org/wiki/Mandelbrot_set"),
            new("Benoit Mandelbrot (Wikipedia)",
                "https://en.wikipedia.org/wiki/Benoit_Mandelbrot"),
            new("Mandelbulb (skytopia)",
                "https://www.skytopia.com/project/fractal/2mandelbulb.html"),
            new("Perturbation theory (K.I. Martin)",
                "https://www.fractalforums.com/announcements-and-news/superfractalthing-arbitrary-precision-mandelbrot-set-rendering-in-java/"),
            new("Avalonia UI",
                "https://avaloniaui.net"),
            new("Vortice.Windows",
                "https://github.com/amerkoleci/Vortice.Windows"),
            new("ffmpeg",
                "https://ffmpeg.org"),
            new("ILGPU",
                "https://www.ilgpu.net"),
            new("FFV1 (Wikipedia)",
                "https://en.wikipedia.org/wiki/FFV1"),
        };

        public string GetSystemInfoText()
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== GPU Adapters (DXGI) ===");
            AppendDxgiAdapters(sb);

            sb.AppendLine();
            sb.AppendLine("=== D3D11 Feature Level ===");
            AppendD3D11FeatureLevel(sb);

            sb.AppendLine();
            sb.AppendLine("=== CPU / OS ===");
            sb.AppendLine($"Logical CPUs:    {Environment.ProcessorCount}");
            sb.AppendLine($"Machine name:    {Environment.MachineName}");
            sb.AppendLine($"User:            {Environment.UserName}");
            sb.AppendLine($"OS:              {Environment.OSVersion}");
            sb.AppendLine($".NET Runtime:    {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Architecture:    {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"System page:     {Environment.SystemPageSize} bytes");

            sb.AppendLine();
            sb.AppendLine("=== Memory ===");
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                sb.AppendLine($"Total physical:  {gcInfo.TotalAvailableMemoryBytes / (1024 * 1024)} MB");
                sb.AppendLine($"Process working: {Environment.WorkingSet / (1024 * 1024)} MB");
                sb.AppendLine($"GC total alloc:  {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            }
            catch (Exception ex) { sb.AppendLine($"  (Memory query failed: {ex.Message})"); }

            sb.AppendLine();
            sb.AppendLine($"SIMD vector width (double): {System.Numerics.Vector<double>.Count}");

            // Phase X.5 / Slice 5.2 — GPU compute (ILGPU) section. Bootstrap
            // populates IlgpuDeviceProbe with a callable that enumerates
            // ctx.Devices the same way Compute.Smoke does; provider stays
            // ILGPU-free so this csproj does not need a direct package ref.
            sb.AppendLine();
            sb.AppendLine("=== GPU Compute (ILGPU) ===");
            string? ilgpu = SafeProbe(IlgpuDeviceProbe);
            sb.AppendLine(string.IsNullOrWhiteSpace(ilgpu)
                ? "  (ILGPU device enumeration not available on this host.)"
                : ilgpu);

            // Phase X.5 / Slice 5.2 — Audio capture backend + capability flags.
            // Bootstrap populates AudioBackendProbe with the active backend's
            // type name + AudioBackendCapabilities so the user can see why
            // System loopback might be greyed in the audio settings dialog.
            sb.AppendLine();
            sb.AppendLine("=== Audio capture backend ===");
            string? audio = SafeProbe(AudioBackendProbe);
            sb.AppendLine(string.IsNullOrWhiteSpace(audio)
                ? "  (Audio backend has not started on this host yet.)"
                : audio);

            return sb.ToString();
        }

        private static string? SafeProbe(Func<string?>? probe)
        {
            if (probe == null) return null;
            try { return probe(); }
            catch (Exception ex) { return $"  (probe failed: {ex.Message})"; }
        }

        private void AppendDxgiAdapters(StringBuilder sb)
        {
            if (_hardwareInfo == null)
            {
                sb.AppendLine("  (GPU enumeration not available on this host.)");
                return;
            }
            _hardwareInfo.AppendGpuAdapters(sb);
        }

        private void AppendD3D11FeatureLevel(StringBuilder sb)
        {
            if (_hardwareInfo == null)
            {
                sb.AppendLine("  (GPU feature-level query not available on this host.)");
                return;
            }
            _hardwareInfo.AppendGpuFeatureLevel(sb);
        }
    }
}
