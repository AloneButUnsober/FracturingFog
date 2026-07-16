// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/ServerInstanceProbe.cs
// Two ways to tell whether a server is already running on this machine:
//   1) Try to open a TcpClient against the configured port (cheap, works
//      cross-process even when the server is owned by another user).
//   2) On Windows, hold a Local\\FF_Server_v1 named mutex inside the
//      hosting process so two servers can't bind the same port silently.
//
// MUTEX SCOPE NOTE:
//   We use the "Local\\" prefix (per-session namespace) rather than
//   "Global\\". Consequences operators should know:
//
//   - Two server processes started by the SAME interactive user in the
//     same logon session are mutually exclusive (intended).
//   - Two server processes started under DIFFERENT user accounts on the
//     same machine each see their own Local\ namespace and can both
//     succeed acquiring the mutex. The port-bind probe is what actually
//     prevents the second one from listening — the mutex is a friendly
//     guard, not the authoritative one.
//   - A server started as a Windows service (Session 0) and a server
//     started from an interactive desktop session are likewise
//     mutually invisible via this mutex.
//   - Switching to "Global\\" would require SeCreateGlobalPrivilege,
//     which non-admin users lack — and the dev bundle is designed to
//     run as a normal user. The port probe is the authoritative gate.

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Server;

public sealed class ServerInstanceProbe : IDisposable
{
    public const string MutexName = "Local\\FracturingFog_Server_v1";

    private readonly Mutex? _mutex;
    private readonly bool _owns;

    private ServerInstanceProbe(Mutex? m, bool owns) { _mutex = m; _owns = owns; }

    public static ServerInstanceProbe AcquireExclusive()
    {
        try
        {
            var m = new Mutex(initiallyOwned: true, MutexName, out bool created);
            if (!created) { try { m.Dispose(); } catch { } return new ServerInstanceProbe(null, false); }
            return new ServerInstanceProbe(m, true);
        }
        catch { return new ServerInstanceProbe(null, false); }
    }

    public bool OwnsExclusive => _owns;

    public static bool IsListening(string host, int port, int timeoutMs = 500)
    {
        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && tcp.Connected;
        }
        catch { return false; }
    }

    /// <summary>Awaitable probe. Avalonia status timers and other UI-thread
    /// callers must use this instead of <see cref="IsListening"/> — the
    /// sync overload's <c>task.Wait(timeoutMs)</c> blocks the dispatcher
    /// for up to <paramref name="timeoutMs"/> milliseconds when the
    /// server is down, producing visible UI lag once per poll.</summary>
    public static async Task<bool> IsListeningAsync(string host, int port, int timeoutMs = 500, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                return tcp.Connected;
            }
            catch (OperationCanceledException) { return false; }
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_mutex == null) return;
        try { if (_owns) _mutex.ReleaseMutex(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
