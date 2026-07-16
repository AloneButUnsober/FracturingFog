// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class OceanDepthMap : CyclingGradientColorMap
    {
        public static string Name => "Ocean Depth";

        public OceanDepthMap()
        {
            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#001F33")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#004F7C")));
            Stops.Add(new ColorStop(0.7f, ColorTranslator.FromHtml("#00A0C6")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#E0FFFF")));
        }
    }

}
