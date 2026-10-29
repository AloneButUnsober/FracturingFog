// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Input/IPointerEditHandler.cs
//
// #1549 — an editor that works directly on the render surface (the Math Plane
// object editor first). FractalInputController offers it left presses, drags,
// double-clicks, hovers and keys before its own pan / recentre / zoom handling;
// an event the editor claims is not also treated as navigation.

namespace FracturingFog.Input
{
    public interface IPointerEditHandler
    {
        /// <summary>Left press. True claims it: the following moves and the release
        /// go to this handler, and no pan starts.</summary>
        bool OnPointerDown(PointerInput e);

        /// <summary>A move during a drag this handler claimed.</summary>
        void OnPointerMove(PointerInput e);

        /// <summary>The release that ends a drag this handler claimed.</summary>
        void OnPointerUp(PointerInput e);

        /// <summary>Left double-click. True claims it (no recentre).</summary>
        bool OnDoubleClick(PointerInput e);

        /// <summary>A move with no button held. Must be cheap.</summary>
        void OnHover(PointerInput e);

        /// <summary>A key on the render surface. True claims it.</summary>
        bool OnKey(KeyInput e);
    }
}
