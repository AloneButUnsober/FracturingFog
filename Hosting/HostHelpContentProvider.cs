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

        // Order mirrors the legacy WinForms FloatingHelp Math tab so users
        // moving between the two shells see the same layout.
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
        };

        public IReadOnlyList<HelpLink> AboutLinks { get; } = new HelpLink[]
        {
            new("Project repository",
                "https://github.com/anthropics/anthropic-cookbook"),
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

            return sb.ToString();
        }

        private static void AppendDxgiAdapters(StringBuilder sb)
        {
            // DXGI is Windows-only. Bail with a friendly note on macOS / Linux
            // so the rest of the system info still renders cleanly.
            if (!OperatingSystem.IsWindows())
            {
                sb.AppendLine("  (DXGI enumeration only available on Windows.)");
                return;
            }

            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                uint idx = 0;
                while (factory.EnumAdapters1(idx, out var adapter).Success)
                {
                    var desc = adapter.Description1;
                    sb.AppendLine($"Adapter {idx}: {desc.Description}");
                    sb.AppendLine($"  Vendor ID:      0x{desc.VendorId:X4}");
                    sb.AppendLine($"  Device ID:      0x{desc.DeviceId:X4}");
                    sb.AppendLine($"  Dedicated VRAM: {desc.DedicatedVideoMemory / (1024 * 1024)} MB");
                    sb.AppendLine($"  Shared RAM:     {desc.SharedSystemMemory / (1024 * 1024)} MB");
                    adapter.Dispose();
                    idx++;
                }
                if (idx == 0) sb.AppendLine("  (No DXGI adapters reported.)");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (DXGI enumeration failed: {ex.Message})");
            }
        }

        private static void AppendD3D11FeatureLevel(StringBuilder sb)
        {
            if (!OperatingSystem.IsWindows())
            {
                sb.AppendLine("  (D3D11 only available on Windows.)");
                return;
            }
            try
            {
                Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    null,
                    Vortice.Direct3D.DriverType.Hardware,
                    Vortice.Direct3D11.DeviceCreationFlags.None,
                    null,
                    out _, out var fl, out _);
                sb.AppendLine($"Max Feature Level: {fl}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (Could not query D3D11 feature level: {ex.Message})");
            }
        }
    }
}
