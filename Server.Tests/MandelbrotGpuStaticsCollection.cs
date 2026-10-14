// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// MandelbrotCalculator's GPU switches (UseGpuPerturbation, UseGpuSeriesApproximation,
/// UseGpuBla, UseGpuDeepOrbit, UseGpuReferenceOrbit) are process-wide statics. A test
/// class that writes one, or asserts a route that depends on one, MUST join this
/// collection: run in parallel, S607 switching UseGpuPerturbation off mid-frame failed
/// S88's "GPU perturbation did not run" check, and S88 switching it on breaks the
/// NotRequested route S1173M expects.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MandelbrotGpuStaticsCollection { public const string Name = "MandelbrotGpuStatics"; }
