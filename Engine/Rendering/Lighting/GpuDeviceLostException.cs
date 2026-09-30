// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>#1044 — a GPU backend's device was removed, hung or reset during a
/// dispatch (typically the OS GPU watchdog, TDR). Backends translate their own
/// device-lost errors into this type so the engine, which cannot reference the
/// backends, can fall back to the CPU path instead of crashing.</summary>
public sealed class GpuDeviceLostException : Exception
{
    public GpuDeviceLostException(string message, Exception? inner = null) : base(message, inner) { }
}
