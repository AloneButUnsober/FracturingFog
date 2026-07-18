// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class PurplebodyColorMap : CyclingGradientColorMap
    {
        public static string Name => "Purple Body Rad";

        public PurplebodyColorMap()
        {
            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#a464a8")));  //Color.Purple));
            Stops.Add(new ColorStop(0.2f, ColorTranslator.FromHtml("#013472")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#016d72")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#017206")));
            Stops.Add(new ColorStop(0.8f, ColorTranslator.FromHtml("#720601")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#a464a8"))); // Color.LightCyan));
        }
    }

}
