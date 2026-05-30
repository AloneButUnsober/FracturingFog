// Server/ServerInstanceProbe.cs
// Two ways to tell whether a server is already running on this machine:
//   1) Try to open a TcpClient against the configured port (cheap, works
//      cross-process even when the server is owned by another user).
//   2) On Windows, hold a Local\\FF_Server_v1 named mutex inside the
//      hosting process so two servers can't bind the same port silently.

using System;
using System.Net.Sockets;
using System.Threading;

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

    public void Dispose()
    {
        if (_mutex == null) return;
        try { if (_owns) _mutex.ReleaseMutex(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
