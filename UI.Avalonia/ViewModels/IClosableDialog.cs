using System;

namespace FracturingFog.UI.Avalonia.ViewModels
{
    /// <summary>
    /// Implemented by dialog view-models that ask their host to close. Under
    /// the Hybrid shell (see Docs/UI-Overhaul-Plan.md), feature views are
    /// <c>UserControl</c>s that no longer own a window, so they cannot close
    /// themselves — the generic <see cref="Services.PanelHostWindow"/> (when
    /// popped out) or the shell (when docked) subscribes to
    /// <see cref="CloseRequested"/> and closes/hides on its behalf.
    ///
    /// Most existing dialog VMs already expose this exact event; implementing
    /// the interface just makes the contract discoverable to the host.
    /// </summary>
    public interface IClosableDialog
    {
        /// <summary>Raised <c>true</c> to close with success (Result populated),
        /// <c>false</c> to cancel.</summary>
        event EventHandler<bool>? CloseRequested;
    }
}
