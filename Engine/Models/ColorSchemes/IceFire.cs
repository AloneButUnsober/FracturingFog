// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class IcefireColorMap : CyclingGradientColorMap
    {
        public static string Name => "Ice Fire";

        public IcefireColorMap()
        {
            Stops.Add(new ColorStop(0.0f, Color.Black));
            Stops.Add(new ColorStop(0.3f, ColorTranslator.FromHtml("#0055FF")));
            Stops.Add(new ColorStop(0.5f, ColorTranslator.FromHtml("#00FFFF")));
            Stops.Add(new ColorStop(0.7f, ColorTranslator.FromHtml("#FF5500")));
            Stops.Add(new ColorStop(1.0f, Color.White));
        }
    }

}
