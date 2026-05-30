// Batch/RemoteBatchRunner.cs
// Headless --batch --remote --connection NAME --render NAME --out PATH path.
// Reuses the saved connection + render preset that the Avalonia FFClient
// dialog edits, prompts for the vault master password on stdin (echo
// suppressed via Console.ReadKey), drives one FFClientConnection round
// trip, and writes the returned bytes to --out.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Client;
using FracturingFog.Server.Protocol;

namespace FracturingFog.Batch;

public static class RemoteBatchRunner
{
    public static int Run(BatchOptions opts)
    {
        var connStore = ClientConnectionStore.LoadOrCreate();
        var preset = RenderOptionsStore.LoadOrCreate().FindByName(opts.RemotePreset!);
        if (preset == null)
        {
            Console.Error.WriteLine($"batch remote: render preset '{opts.RemotePreset}' not found");
            return 5;
        }

        var entry = connStore.Entries.FirstOrDefault(
            e => string.Equals(e.Name, opts.RemoteConnection, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            Console.Error.WriteLine($"batch remote: connection '{opts.RemoteConnection}' not found");
            return 5;
        }

        string? pfxPassword = null;
        if (entry.SealedPfxPassword != null)
        {
            string master = PromptPasswordOnStdin($"Master password (vault for '{entry.Name}'): ");
            try { pfxPassword = connStore.UnlockPfxPassword(entry, master); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"batch remote: vault unlock failed: {ex.Message}");
                return 6;
            }
        }

        RenderRequestDto req = preset.Request;
        // Sanity: never let a saved preset target a blocked fractal type; the
        // server would refuse anyway but we surface the failure earlier and
        // with a clearer message.
        if (string.Equals(req.FractalType, "UserEquation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(req.FractalType, "Sandbox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(req.FractalType, "UserBulb", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"batch remote: fractal type '{req.FractalType}' is not permitted for remote rendering");
            return 7;
        }

        Console.WriteLine($"batch remote → {entry.Host}:{entry.Port}");
        Console.WriteLine($"  preset : {opts.RemotePreset}");
        Console.WriteLine($"  mode   : {req.Mode}");
        Console.WriteLine($"  size   : {req.Width}x{req.Height}");
        Console.WriteLine($"  out    : {opts.OutputPath}");

        try
        {
            return RunAsync(opts, entry, pfxPassword, req).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"batch remote failed: {ex.GetType().Name}: {ex.Message}");
            if (opts.Verbose) Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static async Task<int> RunAsync(BatchOptions opts, ClientConnectionEntry entry,
        string? pfxPassword, RenderRequestDto req)
    {
        await using var conn = await FFClientConnection.ConnectAsync(new FFClientConnection.ConnectOptions
        {
            Host = entry.Host,
            Port = entry.Port,
            ClientCertPath = entry.ClientCertPath,
            ClientCertPassword = pfxPassword,
            ServerCaCertPath = string.IsNullOrEmpty(entry.ServerCaCertPath) ? null : entry.ServerCaCertPath,
        }, CancellationToken.None).ConfigureAwait(false);

        // CLI path always asks for inline bytes — the user gave a local --out.
        req.ReturnMode = "inline";

        RenderResponseDto resp = string.Equals(req.Mode, "video", StringComparison.OrdinalIgnoreCase)
            ? await conn.RenderVideoAsync(req, CancellationToken.None).ConfigureAwait(false)
            : await conn.RenderImageAsync(req, CancellationToken.None).ConfigureAwait(false);

        string? b64 = string.Equals(req.Mode, "video", StringComparison.OrdinalIgnoreCase)
            ? resp.Mp4BytesBase64
            : resp.PngBytesBase64;
        if (string.IsNullOrEmpty(b64))
        {
            Console.Error.WriteLine("batch remote: server returned no bytes");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.OutputPath))!);
        byte[] bytes = Convert.FromBase64String(b64);
        await File.WriteAllBytesAsync(opts.OutputPath, bytes).ConfigureAwait(false);
        Console.WriteLine($"saved {bytes.Length:N0} bytes → {opts.OutputPath}  ({resp.ElapsedMs} ms)");
        return 0;
    }

    private static string PromptPasswordOnStdin(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        return sb.ToString();
    }
}
