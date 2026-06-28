// ServerHost/ClusterEntry.cs
// D-2b: --master / --worker CLI handlers. Stands the cluster bits up
// out of the same hosting layer the single-server --server path uses.
//
//   --master                start an FFServer with a ClusterCoordinator
//                           (WorkerRegistry + JobStore + Dispatcher + Skia
//                           codec) wired. Reuses the role-aware self-signed
//                           dev bundle from CertSelfSignedHelper.
//   --worker --master-host HOST [--master-port N] [--worker-name N]
//                           connect to a master and run tiles via
//                           HostFractalRenderEngine.
//
// Both modes write to %APPDATA%\FracturingFog\cluster-certs\ by default
// (separate from the single-server bundle so a cluster CA never gets
// crossed with the single-server CA accidentally). Operators can point
// at their own PKI with --cluster-certs-dir.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server;
using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Logging;
using FracturingFog.Server.Tls;

namespace FracturingFog.ServerHost;

public static class ClusterEntry
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    private const int ATTACH_PARENT_PROCESS = -1;

    public static int RunMaster(string[] args)
    {
        if (OperatingSystem.IsWindows()) AttachOrAllocConsole();

        var opts = ParseMasterOpts(args, out string? err);
        if (err != null) { Console.Error.WriteLine($"master: {err}"); return 2; }

        var cfg = ServerConfig.LoadOrDefault();
        cfg.Port        = opts.Port;
        cfg.BindAddress = opts.Bind;
        cfg.LogDir      = opts.LogDir ?? ServerConfig.DefaultLogDir();
        cfg.WorkDir     = opts.WorkDir ?? ServerConfig.DefaultWorkDir();
        Directory.CreateDirectory(cfg.LogDir);
        Directory.CreateDirectory(cfg.WorkDir);

        string certDir = opts.CertsDir ?? DefaultClusterCertsDir();
        var bundle = CertSelfSignedHelper.EnsureClusterBundle(certDir);
        Console.WriteLine("cluster dev bundle:");
        Console.WriteLine($"  certs dir : {certDir}");
        Console.WriteLine($"  ca        : {bundle.CaPath}");
        Console.WriteLine($"  master pfx: {bundle.MasterPath}");
        Console.WriteLine($"  worker pfx: {bundle.WorkerPath}");
        Console.WriteLine($"  client pfx: {bundle.ClientPath}");
        Console.WriteLine($"  admin  pfx: {bundle.AdminPath}");

        ServerTrust trust;
        try { trust = ServerCertLoader.Load(bundle.MasterPath, bundle.CaPath); }
        catch (Exception ex) { Console.Error.WriteLine($"cert load failed: {ex.Message}"); return 3; }

        try { FracturingFog.Models.ColorPalette.LoadUserThemes(); } catch { }
        try { FracturingFog.Models.FractalRegionLibrary.Instance.Load(); } catch { }

        using var probe = ServerInstanceProbe.AcquireExclusive();
        if (!probe.OwnsExclusive)
        {
            Console.Error.WriteLine("another FracturingFog server is already running on this host");
            return 4;
        }

        string jobsDir = opts.JobsDir ?? Path.Combine(ServerConfig.AppDataDir(), "master", "jobs");
        string clusterLogDir = Path.Combine(ServerConfig.AppDataDir(), "master-logs");
        Directory.CreateDirectory(jobsDir);
        Directory.CreateDirectory(clusterLogDir);

        using var clusterLog = new ClusterLogger(clusterLogDir);
        var registry  = new WorkerRegistry { HeartbeatIntervalSeconds = 5 };
        var jobStore  = new JobStore(jobsDir);
        var disp      = new TileDispatcher();
        var codec     = new SkiaClusterImageCodec();
        var coord     = new ClusterCoordinator(registry, clusterLog)
        {
            Jobs           = jobStore,
            Dispatcher     = disp,
            Codec          = codec,
            EngineBuildSha = MasterEngineBuildSha(),
            // D-5e — live-tunable cluster knobs. Seed from server-config.json
            // so a master restart picks up whatever the admin UI last saved.
            // cluster.config.set persists back through PersistConfig so the
            // dial sticks across restarts without an out-of-band edit.
            ClusterMaxJobs                  = cfg.ClusterMaxJobs,
            ClusterArtifactRetentionMinutes = cfg.ClusterArtifactRetentionMinutes,
            ClusterTileTargetPixels         = cfg.ClusterTileTargetPixels,
            PersistConfig = snap =>
            {
                cfg.ClusterMaxJobs                  = snap.ClusterMaxJobs;
                cfg.ClusterArtifactRetentionMinutes = snap.ClusterArtifactRetentionMinutes;
                cfg.ClusterTileTargetPixels         = snap.ClusterTileTargetPixels;
                cfg.Save();
            },
        };

        // D-5e — periodic eviction of terminal jobs older than the retention
        // window. Timer drives JobStore.EvictExpired; reads ClusterArtifactRetentionMinutes
        // live so a config.set takes effect on the next tick without restart.
        // 0 = never evict (timer stays armed but skips the call).
        using var evictionTimer = new System.Threading.Timer(_ =>
        {
            int mins = coord.ClusterArtifactRetentionMinutes;
            if (mins <= 0) return;
            try { jobStore.EvictExpired(TimeSpan.FromMinutes(mins)); }
            catch { /* best-effort sweep; next tick retries */ }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        var engine = new HostFractalRenderEngine();
        var server = new FFServer(cfg, engine, trust) { Coordinator = coord };

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("stopping master...");
            lifetime.Cancel();
        };

        Console.WriteLine($"master listening on {cfg.BindAddress}:{cfg.Port}");
        try
        {
            server.RunAsync(lifetime.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (lifetime.IsCancellationRequested) { _ = ex; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"master crashed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        Console.WriteLine("master stopped");
        return 0;
    }

    public static int RunWorker(string[] args)
    {
        if (OperatingSystem.IsWindows()) AttachOrAllocConsole();

        var opts = ParseWorkerOpts(args, out string? err);
        if (err != null) { Console.Error.WriteLine($"worker: {err}"); return 2; }

        string certDir = opts.CertsDir ?? DefaultClusterCertsDir();
        if (!Directory.Exists(certDir))
        {
            Console.Error.WriteLine(
                $"worker: cluster certs dir '{certDir}' does not exist — start the master on this host first, or copy the bundle from the master.");
            return 3;
        }
        var bundle = CertSelfSignedHelper.EnsureClusterBundle(certDir);

        var identity = new WorkerRegisterDto
        {
            WorkerName            = opts.WorkerName ?? Environment.MachineName,
            OsPlatform            = OsTag(),
            CpuModel              = "",
            LogicalCores          = Environment.ProcessorCount,
            TotalRamBytes         = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Gpus                  = new(),
            SupportedFractalTypes = new() { "Mandelbrot","BurningShip","Tricorn","Multibrot","Julia","Phoenix","Newton","Nova","BuddhaBrot" },
            MaxConcurrentTiles    = opts.MaxConcurrentTiles,
            PreferredTilePixels   = opts.PreferredTilePixels,
            EngineBuildSha        = MasterEngineBuildSha(),
            ProtocolVersion       = "1",
        };

        string workDir = opts.WorkDir ?? Path.Combine(ServerConfig.AppDataDir(), "worker-work");
        Directory.CreateDirectory(workDir);

        try { FracturingFog.Models.ColorPalette.LoadUserThemes(); } catch { }
        try { FracturingFog.Models.FractalRegionLibrary.Instance.Load(); } catch { }

        var engine = new HostFractalRenderEngine();
        var agent  = new FFWorkerAgent(new FFWorkerAgent.Options
        {
            MasterHost             = opts.MasterHost!,
            MasterPort             = opts.MasterPort,
            WorkerCertPath         = bundle.WorkerPath,
            MasterCaCertPath       = bundle.CaPath,
            ExpectedMasterHostName = CertSelfSignedHelper.DefaultServerCnDnsName,
            Identity               = identity,
            Engine                 = engine,
            Codec                  = new SkiaClusterImageCodec(),
            WorkDirRoot            = workDir,
        });

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("stopping worker...");
            lifetime.Cancel();
        };

        Console.WriteLine($"worker '{identity.WorkerName}' → {opts.MasterHost}:{opts.MasterPort}");
        agent.Start();
        try { lifetime.Token.WaitHandle.WaitOne(); }
        finally { agent.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        Console.WriteLine("worker stopped");
        return 0;
    }

    public static string DefaultClusterCertsDir()
        => Path.Combine(ServerConfig.AppDataDir(), "cluster-certs");

    public static string MasterEngineBuildSha()
    {
        // Reuse the InformationalVersion baked at compile time. Cluster
        // workers re-present it on register; mismatch refuses the worker
        // (risk #7 in the dev plan).
        try
        {
            var asm = typeof(HostFractalRenderEngine).Assembly;
            var ai  = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (ai != null && !string.IsNullOrEmpty(ai.InformationalVersion))
                return ai.InformationalVersion;
            return asm.GetName().Version?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string OsTag()
        => OperatingSystem.IsWindows() ? "win"
         : OperatingSystem.IsLinux()   ? "linux"
         : OperatingSystem.IsMacOS()   ? "macos"
         : "unknown";

    // ── arg parsing ─────────────────────────────────────────────────────

    public sealed class MasterOptions
    {
        public int     Port      { get; set; } = ServerConfig.DefaultPort;
        public string  Bind      { get; set; } = "127.0.0.1";
        public string? CertsDir  { get; set; }
        public string? LogDir    { get; set; }
        public string? WorkDir   { get; set; }
        public string? JobsDir   { get; set; }
    }

    public sealed class WorkerOptions
    {
        public string? MasterHost          { get; set; }
        public int     MasterPort          { get; set; } = ServerConfig.DefaultPort;
        public string? CertsDir            { get; set; }
        public string? WorkDir             { get; set; }
        public string? WorkerName          { get; set; }
        public int     MaxConcurrentTiles  { get; set; } = 1;
        public int     PreferredTilePixels { get; set; } = 512;
    }

    private static MasterOptions ParseMasterOpts(string[] args, out string? err)
    {
        var o = new MasterOptions();
        err = null;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string v;
            switch (a.ToLowerInvariant())
            {
                case "--port":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    if (!int.TryParse(v, out int p)) { err = $"--port expected int, got '{v}'"; return o; }
                    o.Port = p; break;
                case "--bind":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.Bind = v; break;
                case "--cluster-certs-dir":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.CertsDir = v; break;
                case "--log-dir":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.LogDir = v; break;
                case "--work-dir":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.WorkDir = v; break;
                case "--jobs-dir":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.JobsDir = v; break;
                case "--help": case "-?":
                    PrintMasterUsage(); err = "__help__"; return o;
                default:
                    err = $"unknown argument: {a}"; return o;
            }
        }
        return o;
    }

    private static WorkerOptions ParseWorkerOpts(string[] args, out string? err)
    {
        var o = new WorkerOptions();
        err = null;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string v;
            switch (a.ToLowerInvariant())
            {
                case "--master-host":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.MasterHost = v; break;
                case "--master-port":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    if (!int.TryParse(v, out int p)) { err = $"--master-port expected int, got '{v}'"; return o; }
                    o.MasterPort = p; break;
                case "--cluster-certs-dir":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.CertsDir = v; break;
                case "--work-dir":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.WorkDir = v; break;
                case "--worker-name":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    o.WorkerName = v; break;
                case "--max-concurrent-tiles":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    if (!int.TryParse(v, out int m)) { err = $"--max-concurrent-tiles expected int, got '{v}'"; return o; }
                    o.MaxConcurrentTiles = m; break;
                case "--preferred-tile-pixels":
                    if (!Next(args, ref i, a, out v, out err)) return o;
                    if (!int.TryParse(v, out int t)) { err = $"--preferred-tile-pixels expected int, got '{v}'"; return o; }
                    o.PreferredTilePixels = t; break;
                case "--help": case "-?":
                    PrintWorkerUsage(); err = "__help__"; return o;
                default:
                    err = $"unknown argument: {a}"; return o;
            }
        }
        if (string.IsNullOrEmpty(o.MasterHost))
        { err = "--worker requires --master-host HOST"; return o; }
        return o;
    }

    private static bool Next(string[] a, ref int i, string flag, out string v, out string? err)
    {
        if (i + 1 >= a.Length) { v = ""; err = $"{flag} requires a value"; return false; }
        v = a[++i]; err = null; return true;
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

    private static void PrintMasterUsage()
    {
        Console.WriteLine("Fracturing Fog — cluster master (D-2b)");
        Console.WriteLine("Usage: FracturingFog --master [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --bind ADDR              Listen interface (default 127.0.0.1)");
        Console.WriteLine($"  --port N                 Listen port (default {ServerConfig.DefaultPort})");
        Console.WriteLine("  --cluster-certs-dir PATH Override the cluster cert bundle dir");
        Console.WriteLine("  --log-dir PATH           Session log dir");
        Console.WriteLine("  --work-dir PATH          Per-job scratch dir");
        Console.WriteLine("  --jobs-dir PATH          Job-state root (default %APPDATA%/FracturingFog/master/jobs)");
    }

    private static void PrintWorkerUsage()
    {
        Console.WriteLine("Fracturing Fog — cluster worker (D-2b)");
        Console.WriteLine("Usage: FracturingFog --worker --master-host HOST [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --master-host HOST       Master DNS name or IP (required)");
        Console.WriteLine($"  --master-port N          Master port (default {ServerConfig.DefaultPort})");
        Console.WriteLine("  --cluster-certs-dir PATH Override the cluster cert bundle dir");
        Console.WriteLine("  --work-dir PATH          Per-tile scratch dir");
        Console.WriteLine("  --worker-name NAME       Display name (default hostname)");
        Console.WriteLine("  --max-concurrent-tiles N (default 1)");
        Console.WriteLine("  --preferred-tile-pixels N (default 512)");
    }
}
