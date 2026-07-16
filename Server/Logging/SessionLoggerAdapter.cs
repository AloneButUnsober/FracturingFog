// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Logging/SessionLoggerAdapter.cs
// Bridges the concrete SessionLogger (which owns the FileStream) to the
// engine-side ISessionLog interface.

namespace FracturingFog.Server.Logging;

internal sealed class SessionLoggerAdapter : ISessionLog
{
    private readonly SessionLogger _log;
    public SessionLoggerAdapter(SessionLogger log) => _log = log;
    public void Info(string line) => _log.Info(line);
    public void Warn(string line) => _log.Warn(line);
    public void Err(string line)  => _log.Err(line);
}
