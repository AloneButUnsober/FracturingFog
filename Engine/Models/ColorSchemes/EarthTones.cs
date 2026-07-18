// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class EarthToneMap : CyclingGradientColorMap
    {
        public static string Name => "Earth Tones";

        public EarthToneMap()
        {
            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#2B1B0E")));
            Stops.Add(new ColorStop(0.3f, ColorTranslator.FromHtml("#705438")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#C9A66B")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#F2E9D8")));
        }
    }

}
