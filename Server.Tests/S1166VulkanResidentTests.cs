// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1166 — VulkanComputeKernel counts the device memory its buffers hold (the
// bench's Resident column). Checked against what the escape-time frame
// allocates: 7 buffers, 32 B per pixel plus the 80 B params and 4 B per row,
// each rounded up to the driver's alignment; the count follows a resize down and
// returns to 0 on dispose. Skipped without a Vulkan device.

using System;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1166VulkanResidentTests
{
    private const long Slack = 7 * 64 * 1024;   // per-buffer alignment / granularity

    private static long Requested(int w, int h) => 80 + 4L * h + 32L * w * h;

    private static void Frame(VulkanComputeKernel k, int w, int h)
    {
        int n = w * h;
        k.Run(w, h, -0.5, 0, 3.5 / w, 64, 4.0, new int[n], new float[n], new float[n], new float[n],
            new float[n], new float[n]);
    }

    [Fact]
    public void The_Count_Is_What_The_Frame_Buffers_Hold()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        try
        {
            Assert.Equal(0, k.ResidentBytes);

            Frame(k, 640, 480);
            long big = k.ResidentBytes;
            Assert.InRange(big, Requested(640, 480), Requested(640, 480) + Slack);
            Assert.InRange(k.ResidentDeviceLocalBytes, 0, big);

            Frame(k, 160, 120);   // smaller frame: the buffers are re-created
            long small = k.ResidentBytes;
            Assert.InRange(small, Requested(160, 120), Requested(160, 120) + Slack);

            Frame(k, 160, 120);   // same size: nothing new
            Assert.Equal(small, k.ResidentBytes);
        }
        finally { k.Dispose(); }
        Assert.Equal(0, k.ResidentBytes);
        Assert.Equal(0, k.ResidentDeviceLocalBytes);
    }
}
