// Server/ServerProtocolException.cs
// Carries a structured error code from the engine layer through to the
// FFServer dispatcher, which reflects it into an ErrorDto on the wire.
// Distinguishes guard-rejections (forbidden-fractal, limit-exceeded,
// unknown-region, …) from unexpected exceptions which become "render-failed".

using System;

namespace FracturingFog.Server;

public sealed class ServerProtocolException : Exception
{
    public string Code { get; }
    public ServerProtocolException(string code, string message) : base(message) => Code = code;
}
