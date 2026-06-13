// ServerHost/ServerEntry.cs
// --server CLI handler. Parses flags, ensures the self-signed dev bundle is
// present (or loads operator-supplied PFX paths), loads user themes +
// regions so RenderRequestDto.RegionName / ThemeName resolve, then runs
// FFServer.RunAsync until Ctrl-C.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server;
using FracturingFog.Server.Tls;

namespace FracturingFog.ServerHost;

public static class ServerEntry
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    private const int ATTACH_PARENT_PROCESS = -1;

    public static int Run(string[] args)
    {
        // Phase X.3 / Slice 3.2: gate Win32 console-attach so the call is
        // unreachable on non-Win hosts once this file follows the entry point
        // into FracturingFog.App (net10.0). On Linux/macOS stdout/stderr are
        // already wired to the launching terminal so nothing else is needed.
        if (OperatingSystem.IsWindows())
            AttachOrAllocConsole();

        if (args.Length >= 2 && (args[1] == "--help" || args[1] == "-?"))
        {
            PrintUsage();
            return 0;
        }

        var cfg = ServerConfig.LoadOrDefault();
        if (!TryParse(args, cfg, out string? err))
        {
            Console.Error.WriteLine($"server: {err}");
            Console.Error.WriteLine("Try --server --help");
            return 2;
        }

        cfg.LogDir  ??= ServerConfig.DefaultLogDir();
        cfg.WorkDir ??= ServerConfig.DefaultWorkDir();
        Directory.CreateDirectory(cfg.LogDir);
        Directory.CreateDirectory(cfg.WorkDir);

        int swept = WorkDirSweeper.Sweep(cfg.WorkDir, cfg.WorkDirStaleHours,
            line => Console.WriteLine(line));
        if (swept > 0)
            Console.WriteLine($"workdir sweep: removed {swept} stale job folder(s)");

        string certDir = cfg.EffectiveCertsDir();
        string serverPfx;
        string caPfx;

        if (!string.IsNullOrWhiteSpace(cfg.ServerCertPath) && !string.IsNullOrWhiteSpace(cfg.ClientCaCertPath))
        {
            serverPfx = cfg.ServerCertPath!;
            caPfx = cfg.ClientCaCertPath!;
        }
        else
        {
            var bundle = CertSelfSignedHelper.EnsureBundle(certDir);
            serverPfx = bundle.ServerPath;
            caPfx = bundle.CaPath;
            Console.WriteLine("self-signed dev bundle:");
            Console.WriteLine($"  certs dir  : {certDir}");
            Console.WriteLine($"  ca         : {bundle.CaPath}");
            Console.WriteLine($"  server pfx : {bundle.ServerPath}");
            Console.WriteLine($"  client pfx : {bundle.ClientPath}");
        }

        ServerTrust trust;
        try { trust = ServerCertLoader.Load(serverPfx, caPfx); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cert load failed: {ex.Message}");
            return 3;
        }

        // Same singletons the BatchEntry path warms — region + theme lookups by name.
        try { FracturingFog.Models.ColorPalette.LoadUserThemes(); } catch { }
        try { FracturingFog.Models.FractalRegionLibrary.Instance.Load(); } catch { }

        cfg.Save();

        using var probe = ServerInstanceProbe.AcquireExclusive();
        if (!probe.OwnsExclusive)
        {
            Console.Error.WriteLine("another FracturingFog server is already running on this host");
            return 4;
        }

        var engine = new HostFractalRenderEngine();
        var server = new FFServer(cfg, engine, trust);

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("stopping...");
            lifetime.Cancel();
        };

        try
        {
            server.RunAsync(lifetime.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (lifetime.IsCancellationRequested)
        {
            // Normal Ctrl-C unwind.
            _ = ex;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"server crashed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine("server stopped");
        return 0;
    }

    private static bool TryParse(string[] args, ServerConfig cfg, out string? err)
    {
        err = null;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--port":
                    if (!NextInt(args, ref i, a, out int p, out err)) return false;
                    cfg.Port = p; break;

                case "--bind":
                    if (!Next(args, ref i, a, out string bind, out err)) return false;
                    cfg.BindAddress = bind; break;

                case "--max-minutes":
                    if (!NextInt(args, ref i, a, out int mm, out err)) return false;
                    cfg.MaxMinutes = mm; break;

                case "--allow-override":
                    cfg.AllowOverride = true; break;

                case "--queue-depth":
                    if (!NextInt(args, ref i, a, out int qd, out err)) return false;
                    cfg.QueueDepth = qd; break;

                case "--rate-limit-per-minute":
                    if (!NextInt(args, ref i, a, out int rpm, out err)) return false;
                    cfg.RateLimitPerMinute = rpm; break;

                case "--rate-limit-burst":
                    if (!NextInt(args, ref i, a, out int rb, out err)) return false;
                    cfg.RateLimitBurst = rb; break;

                case "--require-tls13":
                    cfg.RequireTls13 = true; break;

                case "--pin-thumbprint":
                    if (!Next(args, ref i, a, out string pt, out err)) return false;
                    cfg.AllowedClientThumbprints.Add(pt); break;

                case "--workdir-stale-hours":
                    if (!Next(args, ref i, a, out string wsh, out err)) return false;
                    if (!double.TryParse(wsh, System.Globalization.NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double wshv))
                        { err = $"{a} expected number, got '{wsh}'"; return false; }
                    cfg.WorkDirStaleHours = wshv; break;

                case "--cert":
                    if (!Next(args, ref i, a, out string cp, out err)) return false;
                    cfg.ServerCertPath = cp; break;

                case "--client-ca":
                    if (!Next(args, ref i, a, out string ca, out err)) return false;
                    cfg.ClientCaCertPath = ca; break;

                case "--certs-dir":
                    if (!Next(args, ref i, a, out string cd, out err)) return false;
                    cfg.ServerCertsDir = cd; break;

                case "--log-dir":
                    if (!Next(args, ref i, a, out string ld, out err)) return false;
                    cfg.LogDir = ld; break;

                case "--work-dir":
                    if (!Next(args, ref i, a, out string wd, out err)) return false;
                    cfg.WorkDir = wd; break;

                case "--help":
                case "-?":
                    PrintUsage();
                    err = "__help__";
                    return false;

                default:
                    err = $"unknown argument: {a}";
                    return false;
            }
        }
        return true;
    }

    private static bool Next(string[] a, ref int i, string flag, out string v, out string? err)
    {
        if (i + 1 >= a.Length) { v = ""; err = $"{flag} requires a value"; return false; }
        v = a[++i]; err = null; return true;
    }

    private static bool NextInt(string[] a, ref int i, string flag, out int v, out string? err)
    {
        if (!Next(a, ref i, flag, out string s, out err)) { v = 0; return false; }
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            { err = $"{flag} expected integer, got '{s}'"; return false; }
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AttachOrAllocConsole()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError())  { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Fracturing Fog — headless render server (JSON-RPC over mTLS TCP)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  FracturingFog --server [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --bind ADDR          Listen interface (default 127.0.0.1, use 0.0.0.0 for LAN)");
        Console.WriteLine($"  --port N             Listen port (default {ServerConfig.DefaultPort})");
        Console.WriteLine($"  --max-minutes N      Per-job render ceiling (default {ServerConfig.DefaultMaxMinutes})");
        Console.WriteLine("  --allow-override     Honour client requestedMaxMinutes above the default");
        Console.WriteLine("  --queue-depth N      Max concurrent renders (default 1)");
        Console.WriteLine("  --rate-limit-per-minute N");
        Console.WriteLine("                       Sustained accepted connections per remote IP / minute (0 disables)");
        Console.WriteLine("  --rate-limit-burst N Max standing token allowance per IP (default 10)");
        Console.WriteLine("  --require-tls13      Restrict TLS to v1.3 only (default v1.2 + v1.3)");
        Console.WriteLine("  --pin-thumbprint HEX Pin a specific client cert thumbprint; repeat for multiple");
        Console.WriteLine("  --workdir-stale-hours N");
        Console.WriteLine("                       Delete job-* subdirs older than N hours on startup (default 1, 0 disables)");
        Console.WriteLine("  --cert PATH          Server identity PFX (empty password)");
        Console.WriteLine("  --client-ca PATH     Trusted client CA bundle PFX (empty password)");
        Console.WriteLine("  --certs-dir PATH     Override directory for self-signed dev bundle");
        Console.WriteLine("  --log-dir PATH       Per-session log directory");
        Console.WriteLine("  --work-dir PATH      Per-job scratch directory");
        Console.WriteLine();
        Console.WriteLine("If --cert / --client-ca are omitted, a self-signed dev bundle is generated");
        Console.WriteLine("under %APPDATA%/FracturingFog/server-certs/ on first run.");
    }
}
