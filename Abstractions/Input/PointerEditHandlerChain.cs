// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Input/PointerEditHandlerChain.cs
//
// #1716 — several on-canvas editors behind the input controller's single EditHandler
// slot (the Math Plane and Math Plane 3D sections). Presses, double-clicks and keys
// are offered to each handler in order until one claims them; the handler that
// claimed a press gets that drag's moves and release. Hovers go to every handler
// (each ignores the views it does not edit).

using System;
using System.Collections.Generic;

namespace FracturingFog.Input
{
    public sealed class PointerEditHandlerChain : IPointerEditHandler
    {
        private readonly IReadOnlyList<IPointerEditHandler> _handlers;
        private IPointerEditHandler? _dragging;

        public PointerEditHandlerChain(params IPointerEditHandler[] handlers)
            => _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));

        public bool OnPointerDown(PointerInput e)
        {
            _dragging = null;
            foreach (var h in _handlers)
                if (h.OnPointerDown(e)) { _dragging = h; return true; }
            return false;
        }

        public void OnPointerMove(PointerInput e) => _dragging?.OnPointerMove(e);

        public void OnPointerUp(PointerInput e)
        {
            var h = _dragging;
            _dragging = null;
            h?.OnPointerUp(e);
        }

        public bool OnDoubleClick(PointerInput e)
        {
            foreach (var h in _handlers) if (h.OnDoubleClick(e)) return true;
            return false;
        }

        public void OnHover(PointerInput e)
        {
            foreach (var h in _handlers) h.OnHover(e);
        }

        public bool OnKey(KeyInput e)
        {
            foreach (var h in _handlers) if (h.OnKey(e)) return true;
            return false;
        }
    }
}
