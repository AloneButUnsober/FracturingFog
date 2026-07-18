// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class DeepSpaceBlueMap : CyclingGradientColorMap
    {
        public static string Name => "Deep Space";

        public DeepSpaceBlueMap()
        {
            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#000010")));
            Stops.Add(new ColorStop(0.3f, ColorTranslator.FromHtml("#001060")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#00A0FF")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#FFFFFF")));
        }
    }

}
