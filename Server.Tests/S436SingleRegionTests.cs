// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #436 — a slideshow with a single region keeps cycling themes in place instead
// of "changing region" to the same one (re-applying the view, restarting its
// animation, cross-fading the region into itself). SingleRegion.StaysOn is the
// decision SlideshowEngine re-checks at every theme step.

using FracturingFog.Slideshow;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S436SingleRegionTests
{
    [Fact]
    public void One_Region_Pool_Stays()
        => Assert.True(SingleRegion.StaysOn("A", locked: false, travelCourse: null, pool: new[] { "A" }));

    [Fact]
    public void Several_Regions_Move_On()
        => Assert.False(SingleRegion.StaysOn("A", false, null, new[] { "A", "B" }));

    [Fact]
    public void Locked_Region_Stays_Whatever_The_Pool()
        => Assert.True(SingleRegion.StaysOn("A", locked: true, null, new[] { "A", "B", "C" }));

    [Fact]
    public void Pool_Holding_A_Different_Region_Moves_On()
        => Assert.False(SingleRegion.StaysOn("A", false, null, new[] { "B" }));   // pool changed mid-show

    [Fact]
    public void Empty_Or_Missing_Pool_Moves_On()
    {
        Assert.False(SingleRegion.StaysOn("A", false, null, new string[0]));
        Assert.False(SingleRegion.StaysOn("A", false, null, null));
    }

    [Fact]
    public void Travel_Course_Of_One_Region_Stays_And_A_Longer_Course_Moves_On()
    {
        Assert.True(SingleRegion.StaysOn("A", false, new[] { "A" }, pool: new[] { "A", "B" }));
        Assert.True(SingleRegion.StaysOn("A", false, new[] { "A", "A" }, null));
        Assert.False(SingleRegion.StaysOn("A", false, new[] { "A", "B" }, pool: new[] { "A" }));
        Assert.False(SingleRegion.StaysOn("A", false, new string[0], new[] { "A" }));
    }
}
