// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #1793 — tests whose pass / fail depends on the machine not being saturated by the rest of
/// the run, though they share no process state with it: they run after the parallel phase,
/// alone. The cluster stress test (50 clients, 8 workers, 200 async jobs) takes ~2 s alone,
/// but once overran its 2-minute deadlock guard in a full suite, where other tests block
/// thread-pool threads its tasks wait for.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialLoadSensitiveCollection { public const string Name = "SerialLoadSensitive"; }
